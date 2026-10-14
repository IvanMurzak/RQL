// Type adapters (spec §13): member tables, ranking and per-engine type views.
#include "rql/adapters.hpp"

#include <algorithm>
#include <stdexcept>

#include "exec.hpp"

namespace rql {

// ------------------------------------------------------------------ MemberTable

bool MemberTable::contains(const std::string& name) const {
    for (auto& m : members_)
        if (m.name == name) return true;
    return false;
}

MemberDescriptor& MemberTable::member(const std::string& name) {
    for (auto& m : members_)
        if (m.name == name) return m;
    throw std::logic_error("type " + type_.name + " has no member '" + name + "' to configure");
}

MemberTable& MemberTable::add(std::string name, const TypeDescriptor* type, Getter get, Setter set, Validator validate,
                              std::string description, bool owning, bool nullable) {
    if (name.empty()) throw std::logic_error("virtual member name required");
    if (!get) throw std::logic_error("virtual member '" + name + "' needs a getter");
    if (set && type_.kind == TypeKind::Struct)
        throw std::logic_error("virtual setters on value type " + type_.name + " would write to a copy; expose a writable field instead");
    // Callbacks receive a pointer typed as the adapter's target (a base of, or the configured type).
    const TypeDescriptor* from = &type_;
    const TypeDescriptor* to = current_target_ ? current_target_ : &type_;
    auto up = [from, to](void* self) -> void* {
        if (from == to) return self;
        void* p = from->upcast(self, to);
        if (!p) throw std::logic_error("cannot view " + from->name + " as " + to->name);
        return p;
    };
    MemberDescriptor m;
    m.name = std::move(name);
    m.type = type;
    m.nullable = nullable;
    m.owning = owning;
    m.description = std::move(description);
    m.getter = [up, get](void* self) { return get(up(self)); };
    if (set) m.setter = [up, set](void* self, const Value& v) { set(up(self), v); };
    if (validate) m.validate = [up, validate](void* self, const Value& v) { return validate(up(self), v); };
    for (auto& existing : members_) {
        if (existing.name == m.name) {
            existing = std::move(m);  // replace in place (keeps the declaration order)
            return *this;
        }
    }
    members_.push_back(std::move(m));
    return *this;
}

MemberTable& MemberTable::hide(const std::vector<std::string>& names) {
    members_.erase(std::remove_if(members_.begin(), members_.end(),
                                  [&](const MemberDescriptor& m) { return std::find(names.begin(), names.end(), m.name) != names.end(); }),
                   members_.end());
    return *this;
}

MemberTable& MemberTable::only(const std::vector<std::string>& names) {
    members_.erase(std::remove_if(members_.begin(), members_.end(),
                                  [&](const MemberDescriptor& m) { return std::find(names.begin(), names.end(), m.name) == names.end(); }),
                   members_.end());
    return *this;
}

MemberTable& MemberTable::read_only(const std::vector<std::string>& names) {
    for (auto& n : names) member(n).readonly = true;
    return *this;
}

MemberTable& MemberTable::reference(const std::vector<std::string>& names) {
    for (auto& n : names) member(n).owning = false;
    return *this;
}

MemberTable& MemberTable::explicit_only(const std::vector<std::string>& names) {
    for (auto& n : names) member(n).explicit_only = true;
    return *this;
}

MemberTable& MemberTable::describe(const std::string& name, std::string description) {
    member(name).description = std::move(description);
    return *this;
}

// ------------------------------------------------------------------ views

const MemberDescriptor* TypeView::find(const std::string& name) const {
    for (auto& m : members)
        if (m.name == name) return &m;
    const MemberDescriptor* hit = nullptr;
    for (auto& m : members) {
        if (iequals(m.name, name)) {
            if (hit) return nullptr;  // ambiguous
            hit = &m;
        }
    }
    return hit;
}

int adapter_rank(const TypeAdapter& adapter, const TypeDescriptor& type) {
    if (adapter.target) {
        int d = 0;
        for (const TypeDescriptor* t = &type; t; t = t->base, ++d)
            if (t == adapter.target) return 2 * d;
        return -1;
    }
    if (!adapter.interface_name.empty()) {
        for (const TypeDescriptor* t = &type; t; t = t->base)
            for (auto& n : t->also_is)
                if (iequals(n, adapter.interface_name)) return 10000;
    }
    return -1;
}

namespace detail {

std::vector<const TypeAdapter*> applicable_adapters(const std::vector<std::unique_ptr<TypeAdapter>>& all, const TypeDescriptor& type) {
    struct Hit {
        const TypeAdapter* a;
        int rank;
        size_t index;
    };
    std::vector<Hit> hits;
    for (size_t i = 0; i < all.size(); ++i) {
        int r = adapter_rank(*all[i], type);
        if (r >= 0) hits.push_back({all[i].get(), r, i});
    }
    std::stable_sort(hits.begin(), hits.end(), [](const Hit& x, const Hit& y) {
        if (x.rank != y.rank) return x.rank < y.rank;
        if (x.a->priority != y.a->priority) return x.a->priority > y.a->priority;
        return x.index < y.index;
    });
    std::vector<const TypeAdapter*> out;
    for (auto& h : hits) out.push_back(h.a);
    return out;
}

std::unique_ptr<TypeView> build_view(TypeRegistry& registry, const TypeDescriptor& type,
                                     const std::vector<std::unique_ptr<TypeAdapter>>& adapters) {
    auto view = std::make_unique<TypeView>();
    view->type = &type;
    view->members = type.members;
    view->adapters = applicable_adapters(adapters, type);
    if (!view->adapters.empty() && (type.kind == TypeKind::Object || type.kind == TypeKind::Struct)) {
        MemberTable table(registry, type, view->members);
        // Least → most specific, so a subclass adapter refines what a base adapter configured.
        for (size_t i = view->adapters.size(); i-- > 0;) {
            const TypeAdapter* a = view->adapters[i];
            if (!a->configure_members) continue;
            table.set_current_target(a->target);
            a->configure_members(table);
        }
    }
    for (auto* a : view->adapters) {
        if (!a->description.empty()) {
            view->description = a->description;
            break;
        }
    }
    if (view->description.empty()) view->description = type.description;
    // Identity / display are recomputed after adapters (pointers into the final member list).
    view->id = type.id_member_in(view->members);
    view->display = type.display_member_in(view->members);
    return view;
}

}  // namespace detail
}  // namespace rql
