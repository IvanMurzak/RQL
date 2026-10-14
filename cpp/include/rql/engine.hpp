// Host-facing RQL runtime over live C++ objects (mirrors dotnet/Rql.Execution/RqlEngine.cs).
#pragma once

#include <array>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "rql/adapters.hpp"
#include "rql/codec.hpp"
#include "rql/plan.hpp"
#include "rql/reflection.hpp"
#include "rql/result.hpp"
#include "rql/value.hpp"

namespace rql {

namespace detail {
class Exec;
struct AdaptedType;
class TypeView;
}

struct SourceRegistration {
    std::string name;
    std::function<Value()> getter;
    std::string description;
};

/// Per-item evaluator handed to extension stages: evaluates an argument expression with $ = item.
using ArgEvaluator = std::function<Value(const Value& item)>;
/// Extension stage: receives the (materialized) input values, returns the output values.
using StageHandler = std::function<std::vector<Value>(const std::vector<Value>& input, const std::vector<ArgEvaluator>& args,
                                                      const std::map<std::string, ArgEvaluator>& named)>;
using FunctionHandler = std::function<Value(const std::vector<Value>& args)>;

/// One change made (or about to be made) by `set` or `call`, as seen by host write hooks (spec §13).
struct MutationRecord {
    Value item;    // the mutation target (a stream item)
    Value owner;   // the object whose state changes: the last reference object on the written path
    std::string member;  // the written path (set) or the method signature (call)
    Value old_value;     // set only
    Value new_value;     // set only
    const MethodDescriptor* method = nullptr;  // call only
};
using MutationHook = std::function<void(const std::vector<MutationRecord>& records)>;

struct MutationPolicy {
    /// Host-level switch. Requests must additionally set QueryOptions::allow_mutation.
    bool enabled = false;
    /// Optional extra allow-check per (declaring object type, member) → permission-denied when false.
    std::function<bool(const TypeDescriptor& owner, const MemberDescriptor& member)> can_write;
    int max_targets = 20;
    /// Host ceiling no request's maxMutationTargets can exceed.
    int hard_max_targets = 1000;
    /// After planning, before any write (not on dry-run): e.g. record undo for the owners. Throwing (any
    /// exception) vetoes the whole mutation → permission-denied, nothing changed.
    MutationHook before_write;
    /// After every write succeeded (not on dry-run or rollback; for a failing call, the calls that completed):
    /// e.g. mark the owners dirty. Throwing → getter-threw warning; the writes stay applied.
    MutationHook committed;
};

struct EngineDefaults {
    int max_visited = 1000000;
    int max_rows = 50;
    int max_list_items = 20;
    int max_depth = 16;
    int timeout_ms = 2000;
    int max_output_bytes = 65536;
    int default_recurse_depth = 8;
    int hard_max_visited = 5000000;
    int hard_max_rows = 10000;
    int hard_timeout_ms = 30000;
};

class Engine {
public:
    explicit Engine(TypeRegistry& types);
    ~Engine();
    Engine(const Engine&) = delete;
    Engine& operator=(const Engine&) = delete;

    TypeRegistry& types() const { return *types_; }
    EngineDefaults& defaults() { return defaults_; }
    const EngineDefaults& defaults() const { return defaults_; }
    MutationPolicy& mutations() { return mutations_; }
    const MutationPolicy& mutations() const { return mutations_; }
    std::string runtime_name = "cpp";

    /// Liveness check for explicitly destroyed objects (Unreal/Unity style). Unset = trust pointers
    /// resolved by search; session handles ("Type@n") are then re-validated by reachability.
    std::function<bool(const Value& object)> is_alive;
    /// Resolve "Type#id" (type short name, id text). Return missing/null when not found.
    /// Unset: bounded search over owning members of all sources.
    std::function<Value(const std::string& type, const std::string& id)> resolve_stable_ref;
    /// When false, $path is tracked only for plans that reference $path.
    bool always_track_paths = false;

    // Every Engine member function is noexcept: exceptions used internally never cross this API.
    // Registration with an invalid name is ignored and reported through last_error().

    Engine& add_source(const std::string& name, std::function<Value()> getter, std::string description = {}) noexcept;
    /// Convenience: a registered object exposed as a source.
    template <class T>
    Engine& add_object_source(const std::string& name, T* object, std::string description = {}) noexcept {
        TypeRegistry* r = types_;
        return add_source(name, [r, object]() { return r->wrap(object); }, std::move(description));
    }
    /// Namespaced extension function "ns.fn".
    Engine& add_function(const std::string& name, FunctionHandler fn) noexcept;
    /// Namespaced extension stage "ns.stage".
    Engine& add_stage(const std::string& name, StageHandler handler) noexcept;
    /// Register a type adapter (spec §13). Register adapters before running queries (not concurrently
    /// with them). An adapter without a target or interface name is rejected via last_error().
    Engine& add_adapter(TypeAdapter adapter) noexcept;
    /// Ordered adapters applying to a type, most specific first: "name (target T, rank r, priority p)".
    std::vector<std::string> explain(const TypeDescriptor& type) const noexcept;
    bool has_adapters() const noexcept { return !adapters_.empty(); }
    /// Never expose members whose type (or element / key type) matches (spec §13 Exclusion). Configure
    /// before queries run; several predicates combine with "or".
    Engine& exclude_types(TypeExclusion excluded) noexcept;

    /// Last registration error (empty when the last registration succeeded).
    const std::string& last_error() const noexcept { return last_error_; }

    const std::vector<SourceRegistration>& sources() const noexcept { return sources_; }
    const SourceRegistration* find_source(const std::string& name) const noexcept;  // exact, then unique case-insensitive
    const std::map<std::string, FunctionHandler>& functions() const noexcept { return functions_; }
    const std::map<std::string, StageHandler>& stages() const noexcept { return stages_; }

    /// Execute a plan. Any failure (including host callbacks throwing) → ok=false with a diagnostic.
    QueryResult execute(const QueryPlan& plan, const Budgets& budgets = {}, const QueryOptions& options = {}) noexcept;
    /// Decode + execute a plan JSON text; codec errors become an error result.
    QueryResult execute_json(const std::string& plan_json, const Budgets& budgets = {}, const QueryOptions& options = {}) noexcept;
    /// Transport envelope {"rql","plan","budgets","options"} as JSON text or value (text requests → unsupported).
    QueryResult execute_request(const Json& request) noexcept;
    QueryResult execute_request(const std::string& request_json) noexcept;

    /// Stable reference string: "Type#id" (id member) or session handle "Type@n".
    std::string ref_of(const Value& v) noexcept;
    /// Resolve a reference string (copied display forms tolerated). On failure returns missing and sets error.
    Value resolve_ref(const std::string& reference, std::string& error) noexcept;
    void clear_session_handles() noexcept;

    /// Objects built from object literals that this engine keeps alive (owned per written slot, or pinned).
    size_t retained_literal_objects() noexcept;

    /// Capability manifest (spec/capability-manifest.schema.json). Null Json on internal failure.
    Json capabilities() noexcept;
    /// Type descriptor JSON (capability-manifest typeDescriptor).
    Json describe_type(const TypeDescriptor& t) const noexcept;

private:
    friend class detail::Exec;

    struct HandleKey {
        const void* ptr;
        const void* type;
        bool operator==(const HandleKey& o) const { return ptr == o.ptr && type == o.type; }
    };
    struct HandleHash {
        size_t operator()(const HandleKey& k) const {
            return std::hash<const void*>()(k.ptr) * 31u ^ std::hash<const void*>()(k.type);
        }
    };
    struct HandleTarget {
        Value value;
    };

    TypeRegistry* types_;
    EngineDefaults defaults_;
    MutationPolicy mutations_;
    std::vector<SourceRegistration> sources_;
    std::map<std::string, FunctionHandler> functions_;
    std::map<std::string, StageHandler> stages_;
    std::mutex handle_mutex_;
    std::unordered_map<HandleKey, int64_t, HandleHash> handles_;
    std::unordered_map<int64_t, HandleTarget> handle_targets_;
    int64_t next_handle_ = 0;
    std::string last_error_;
    std::vector<std::unique_ptr<TypeAdapter>> adapters_;
    std::array<bool, 6> caps_{};  // some adapter implements the capability (detail::Capability order)
    TypeExclusion excluded_;
    // Objects constructed from object literals (spec §4.5) that committed writes linked into the graph:
    // owned per written slot (item identity + path), released when a later literal replaces them and no
    // link to them remains; pinned forever when that cannot be proven (rollback failures, call arguments).
    std::mutex adopted_mutex_;
    std::map<std::pair<const void*, std::string>, std::vector<std::shared_ptr<void>>> adopted_;
    std::vector<std::shared_ptr<void>> pinned_;
    void adopt_slot(const Value& item, const std::string& path, std::vector<std::shared_ptr<void>>& holders, detail::Exec* x);
    void pin(std::vector<std::shared_ptr<void>>& holders);
    // Object-literal "$type" lookups (spec §4.5): per target type, name → registered subtypes of its kind.
    mutable std::mutex subtypes_mutex_;
    mutable std::unordered_map<const TypeDescriptor*, std::unordered_map<std::string, std::vector<const TypeDescriptor*>>> subtypes_;
    mutable size_t subtypes_registered_ = 0;
    std::vector<const TypeDescriptor*> subtypes_named(const TypeDescriptor* target, const std::string& name) const;
    /// Whether any member (owning or reference) of anything reachable from the sources points at `p`.
    bool linked_anywhere(const void* p, detail::Exec* x);
    /// Some adapter, or a type exclusion that actually removes a member of a registered type: only then
    /// may types differ from their descriptors (adapter-free engines read descriptors directly).
    bool adapts() const;
    mutable std::mutex exclusion_mutex_;
    mutable size_t exclusion_scanned_ = 0;  // registered-type count of the last scan
    mutable bool exclusion_hits_ = false;

    mutable std::mutex adapted_mutex_;
    mutable std::unordered_map<const TypeDescriptor*, std::unique_ptr<detail::AdaptedType>> adapted_;

    // Internals (may throw; only called inside execution or from noexcept wrappers). `x` is the running
    // query when there is one: its per-query cache is used and visits are counted.
    /// Adaptation of a type (cached, locked); nullptr when no adapter applies. Throws std::logic_error
    /// for adapter configuration bugs (the running query then fails with internal-error).
    const detail::AdaptedType* adapted(const TypeDescriptor* type) const;
    detail::TypeView view_of(const TypeDescriptor* type, detail::Exec* x) const;
    /// THE liveness definition (spec §13): the first adapter verdict, else the host is_alive hook;
    /// nullopt = undecided (treated as alive).
    std::optional<bool> liveness(const Value& object, detail::Exec* x);
    bool alive(const Value& object, detail::Exec* x) { return liveness(object, x).value_or(true); }
    /// "#id" part of a stable reference: adapter stable_id first, then the id member; nullopt = none.
    std::optional<std::string> stable_id_of(const Value& object, detail::Exec* x);
    Value resolve_ref(const std::string& reference, std::string& error, detail::Exec* x);
    /// `fallback`: the trimmed display form, tried when the exact text does not resolve (stable refs of the
    /// same type are matched in ONE source-search pass, exact id preferred).
    Value resolve_ref_core(const std::string& reference, std::string& error, detail::Exec* x, const std::string* fallback);
    Value check_resolved(const Value& found, const std::string& reference, const std::string& type, std::string& error, detail::Exec* x);
    /// Source search for a stable id (`alt_id`: a second id accepted when the first is absent); cached per query.
    Value find_stable(const std::string& type, const std::string& id, const std::string* alt_id, detail::Exec* x);

    int64_t handle_of(const Value& v);
    Value search_sources(const std::string& type, const std::string& id, const std::string* alt_id, const Value* identity, detail::Exec* x);
    Json describe_type_impl(const TypeDescriptor& t) const;
    Json capabilities_impl();
    std::string ref_of_impl(const Value& v, detail::Exec* x);
};

}  // namespace rql
