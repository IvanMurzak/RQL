// Type adapters (spec §13): host extensions that customise how values of a type family are read
// (virtual / hidden / explicit-only members), shown (encode), written (coerce + validation),
// compared, identified (stable id) and checked for liveness — without changing the plan format.
//
// Adapters are registered per Engine (Engine::add_adapter). The engine derives an adapted TypeView of
// each TypeDescriptor, so one TypeRegistry can serve engines with different adapter sets.
//
// Applicability: an adapter targets a descriptor (exact type, or a base: applies to subclasses) or
// an interface name (matched against TypeDescriptor::also_is). Order, most specific first: base-chain
// distance d (rank 2d), then interfaces (rank 10000); ties by priority (higher first), then
// registration order. There is no open-generic tier in C++ (spec §13 allows omitting it).
//
// This is the seam an Unreal provider uses for FVector/FRotator encoding & literals, UObject
// IsValid / pending-kill liveness, and GUID-based stable ids.
#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "rql/json.hpp"
#include "rql/reflection.hpp"
#include "rql/value.hpp"

namespace rql {

/// Outcome of a capability that may decline: not handled (defer), succeeded, or failed with a reason.
enum class AdapterOutcome : uint8_t { NotHandled, Ok, Failed };

/// What an encode callback can use.
class EncodeContext {
public:
    EncodeContext(std::function<Json(const Value&, int)> encode, std::function<Json(const Value&)> summary, int depth)
        : encode_(std::move(encode)), summary_(std::move(summary)), depth_(depth) {}
    /// Nesting depth of the value being encoded (0 = a top-level cell).
    int depth() const { return depth_; }
    /// Encode another value with full adapter support (e.g. a member of this value).
    Json encode_value(const Value& v) const { return encode_(v, depth_ + 1); }
    /// The standard object summary {"$ref": ..., "name": ...}.
    Json summary(const Value& object) const { return summary_(object); }

private:
    std::function<Json(const Value&, int)> encode_;
    std::function<Json(const Value&)> summary_;
    int depth_;
};

/// Validation of a coerced value before any write: return a rejection reason, or empty to accept.
using Validator = std::function<std::string(void* self, const Value& value)>;

/// The member table of one runtime type while adapters shape it. Errors (configuring a member that
/// does not exist, virtual setters on value types) throw std::logic_error inside the engine, which
/// fails the query with `internal-error` — they are host bugs.
class MemberTable {
public:
    MemberTable(TypeRegistry& registry, const TypeDescriptor& type, std::vector<MemberDescriptor>& members)
        : registry_(registry), type_(type), members_(members) {}

    /// The runtime type whose members are being configured.
    const TypeDescriptor& type() const { return type_; }
    TypeRegistry& registry() const { return registry_; }
    const std::vector<MemberDescriptor>& members() const { return members_; }
    bool contains(const std::string& name) const;

    /// Add (or replace) a virtual member. `self` passed to get/set/validate is a pointer to the
    /// ADAPTER's target type (upcast from the configured type). set empty = read-only. Virtual
    /// members are non-owning by default (computed values are not structural children).
    MemberTable& add(std::string name, const TypeDescriptor* type, Getter get, Setter set = {}, Validator validate = {},
                     std::string description = {}, bool owning = false, bool nullable = false);

    /// Typed read-only virtual member: get(T&) -> R (R any Traits-supported type).
    template <class T, class G>
    MemberTable& computed(std::string name, G get, std::string description = {}) {
        using R = std::decay_t<decltype(get(std::declval<T&>()))>;
        TypeRegistry* r = &registry_;
        return add(std::move(name), Traits<R>::type(*r), [r, get](void* self) -> Value {
            R tmp = get(*static_cast<T*>(self));
            return snapshot(Traits<R>::get(*r, tmp));
        }, {}, {}, std::move(description), false, Traits<R>::nullable);
    }

    /// Typed writable virtual member: get(T&) -> R, set(T&, R) (may throw → rollback),
    /// validate(T&, const R&) -> std::string reason (empty = ok; pass nullptr for none).
    template <class T, class G, class S, class V>
    MemberTable& property(std::string name, G get, S set, V validate, std::string description = {}) {
        using R = std::decay_t<decltype(get(std::declval<T&>()))>;
        TypeRegistry* r = &registry_;
        Validator val;
        if constexpr (!std::is_same_v<V, std::nullptr_t>) {
            val = [r, validate](void* self, const Value& v) -> std::string {
                R tmp{};
                Traits<R>::set(*r, tmp, v);
                return validate(*static_cast<T*>(self), tmp);
            };
        }
        return add(
            std::move(name), Traits<R>::type(*r),
            [r, get](void* self) -> Value {
                R tmp = get(*static_cast<T*>(self));
                return snapshot(Traits<R>::get(*r, tmp));
            },
            [r, set](void* self, const Value& v) {
                R tmp{};
                Traits<R>::set(*r, tmp, v);
                set(*static_cast<T*>(self), std::move(tmp));
            },
            std::move(val), std::move(description), false, Traits<R>::nullable);
    }

    /// Remove members entirely (unreadable AND unwritable). Absent names are ignored.
    MemberTable& hide(const std::vector<std::string>& names);
    /// Keep only the named members (whitelist).
    MemberTable& only(const std::vector<std::string>& names);
    MemberTable& read_only(const std::vector<std::string>& names);
    /// Non-owning references: generic recurse and ref search do not follow them.
    MemberTable& reference(const std::vector<std::string>& names);
    /// Read only when a path names the member: skipped by generic recurse, inline/struct encoding and
    /// ref search; `describe` lists it with "explicitOnly": true.
    MemberTable& explicit_only(const std::vector<std::string>& names);
    MemberTable& describe(const std::string& name, std::string description);

    /// Internal: the adapter being applied (its target receives `self`).
    void set_current_target(const TypeDescriptor* target) { current_target_ = target; }

private:
    TypeRegistry& registry_;
    const TypeDescriptor& type_;
    std::vector<MemberDescriptor>& members_;
    const TypeDescriptor* current_target_ = nullptr;
    MemberDescriptor& member(const std::string& name);
};

/// A type adapter. Every capability is optional (an empty std::function defers to the next adapter,
/// then to RQL's default). Callbacks may be noexcept; exceptions thrown by them are contained by the
/// engine (→ internal-error, or rollback for setters).
struct TypeAdapter {
    std::string name;                    // for explain() and messages
    const TypeDescriptor* target = nullptr;  // exact type or base class
    std::string interface_name;          // alternatively: any type whose also_is contains this name
    int priority = 0;                    // tie-break among equal specificity (higher first)
    std::string description;             // one line for schemas / describe (first non-empty wins)

    /// Shape the member table (applied least → most specific).
    std::function<void(MemberTable&)> configure_members;
    /// Replace the encoding of a value of the type; nullopt defers.
    std::function<std::optional<Json>(const Value& value, const EncodeContext& ctx)> encode;
    /// Convert a query-supplied value to `target` (the adapter's type or a subtype) on writes.
    std::function<AdapterOutcome(const Value& value, const TypeDescriptor& target, Value& result, std::string& reason)> coerce;
    /// Equality / ordering of a value of the type with any other value; nullopt defers.
    std::function<std::optional<bool>(const Value& value, const Value& other)> equals;
    std::function<std::optional<int>(const Value& value, const Value& other)> compare;
    /// Stable id for $ref ("Type#id") and ref resolution; nullopt defers to the id member / handles.
    std::function<std::optional<std::string>(const Value& value)> stable_id;
    /// Liveness verdict (destroyed objects → stale-ref); nullopt defers to Engine::is_alive.
    std::function<std::optional<bool>(const Value& value)> is_alive;
};

/// Typed view of an object/struct value as T (upcast through registered bases); nullptr if not a T.
template <class T>
T* value_as(TypeRegistry& registry, const Value& v) {
    if (!(v.is_object() || v.is_struct()) || !v.type()) return nullptr;
    return static_cast<T*>(v.type()->upcast(v.ptr(), registry.descriptor<T>()));
}

/// The adapted view of a type for one engine: members after adapters, id/display recomputed,
/// applicable adapters (most specific first) and the effective description.
struct TypeView {
    const TypeDescriptor* type = nullptr;
    std::vector<MemberDescriptor> members;
    const MemberDescriptor* id = nullptr;
    const MemberDescriptor* display = nullptr;
    std::vector<const TypeAdapter*> adapters;
    std::string description;

    /// Exact name, then a unique case-insensitive match.
    const MemberDescriptor* find(const std::string& name) const;
    /// Identity members are always read-only through RQL.
    bool member_writable(const MemberDescriptor& m) const { return m.has_setter() && &m != id; }
};

/// Specificity rank of `adapter` for `type` (lower = more specific); -1 = not applicable.
int adapter_rank(const TypeAdapter& adapter, const TypeDescriptor& type);

}  // namespace rql
