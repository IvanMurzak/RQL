// Permissioned, all-or-nothing `set` / `call` (spec §8). Mirrors dotnet/Rql.Execution/Mutator.cs.
#include <cmath>
#include <stdexcept>

#include "exec.hpp"

namespace rql {
namespace detail {

namespace {

void check_policy(Exec& x, const std::string& op) {
    if (!x.engine.mutations().enabled)
        throw RqlException("permission-denied", "'" + op + "' is not allowed: this host does not permit mutations");
    if (!x.options.allow_mutation)
        throw RqlException("permission-denied", "'" + op + "' requires a mutation request (use the mutate tool / options.allowMutation)");
}

std::vector<Value> targets(Exec& x, Stream& stream, const std::string& op) {
    std::vector<Value> list;
    Item it;
    while (stream.next(it)) {
        if (it.value.is_null()) continue;
        if (it.value.is_row())
            throw RqlException("invalid-plan", "'" + op + "' needs objects, not projected rows; remove 'select' before '" + op + "'");
        if (it.value.is_entry())
            throw RqlException("invalid-plan", "'" + op + "' cannot target map entries; set the map member with a key path instead (Stats[\"key\"] = ...)");
        const Value& v = it.value;
        if (v.is_struct() || v.is_number() || v.is_string() || v.is_bool() || v.is_enum())
            throw RqlException("invalid-plan",
                               "'" + op + "' needs objects with identity; '" + type_name_of(v) + "' items are value copies, so the change would be lost",
                               "set the member from its owner instead, e.g. objects | where ... | set Transform.Position.x = 0");
        list.push_back(it.value);
        if (static_cast<int64_t>(list.size()) > x.max_mutation_targets)
            throw RqlException("mutation-limit",
                               "more than " + std::to_string(x.max_mutation_targets) + " objects matched '" + op + "'; nothing was changed",
                               "narrow the query (where / first / ref(...)) or raise budgets.maxMutationTargets if the bulk change is intended");
    }
    return list;
}

std::string label_of(const Expr& p) {
    std::string s;
    for (size_t i = 0; i < p.segments.size(); ++i) {
        const Segment& seg = p.segments[i];
        s += seg.kind == SegmentKind::Member ? (i == 0 ? "" : ".") + seg.name : seg.to_string();
    }
    return s;
}

std::string raw_label(const std::vector<Segment>& segs) {
    std::string s;
    for (auto& seg : segs) s += seg.to_string();
    return s;
}

struct Slot {
    enum Kind { Member, ListIndex, MapKey } kind = Member;
    const MemberDescriptor* member = nullptr;
    const TypeDescriptor* owner = nullptr;
    const MemberDescriptor* id = nullptr;  // identity member of the owner's view (always read-only)
    int64_t index = 0;
    Value key;
    const TypeDescriptor* type = nullptr;
    bool nullable = false;

    size_t idx(const Value& c) const {
        int64_t n = static_cast<int64_t>(c.list().size());
        return static_cast<size_t>(index < 0 ? n + index : index);
    }

    Value get(const Value& c) const {
        switch (kind) {
            case Member: return member->getter(c.ptr());
            case ListIndex: return c.list().at(idx(c));
            case MapKey: {
                Value out;
                return c.map().find(key, out) == MapLookup::Found ? out : Value::missing();
            }
        }
        return Value::missing();
    }

    void set(const Value& c, const Value& v) const {
        switch (kind) {
            case Member: member->setter(c.ptr(), v); break;
            case ListIndex: c.list().set(idx(c), v); break;
            case MapKey:
                if (v.is_missing()) c.map().erase(key);
                else c.map().set(key, v);
                break;
        }
    }
};

Slot slot_of(Exec& x, const Value& container, const Segment& seg, const std::string& label) {
    Slot s;
    if (container.is_map()) {
        if (!container.map().writable())
            throw RqlException("unsupported", "map type " + friendly_type_of(container) + " does not support writes");
        if (seg.kind == SegmentKind::Member && iequals(seg.name, "Count"))
            throw RqlException("readonly-member", "'" + label + "': Count of a map is read-only (use [\"Count\"] to address a key literally named Count)");
        Value key;
        if (seg.kind == SegmentKind::Member) {
            key = Value::string(seg.name);
            // Writes address an existing key case-insensitively, like reads do (spec §3); new keys keep the given spelling.
            Value tmp;
            if (container.map().find(key, tmp) == MapLookup::NotFound) {
                container.map().for_each([&](const Value& k, const Value&) {
                    if (k.is_string() && iequals(k.as_string(), seg.name)) {
                        key = k;
                        return false;
                    }
                    return true;
                });
            }
        }
        else if (seg.kind == SegmentKind::Key) key = seg.key.type == Literal::Type::String ? Value::string(seg.key.s) : Value::number(seg.key.n);
        else if (seg.kind == SegmentKind::Index) key = Value::number(static_cast<double>(seg.index));
        if (key.is_missing() || !container.map().accepts_key(key)) throw RqlException("coercion-failed", "invalid map key in '" + label + "'");
        s.kind = Slot::MapKey;
        s.key = key;
        const TypeDescriptor* mt = container.map().type();
        s.type = mt ? mt->element : nullptr;
        s.nullable = mt ? mt->element_nullable : true;
        return s;
    }
    if (container.is_list()) {
        if (seg.kind != SegmentKind::Index)
            throw RqlException("invalid-plan", "'" + label + "': lists can only be indexed ([i]) in set targets");
        if (!container.list().writable()) throw RqlException("readonly-member", "'" + label + "': collection is not writable by index");
        int64_t n = static_cast<int64_t>(container.list().size());
        int64_t i = seg.index < 0 ? n + seg.index : seg.index;
        if (i < 0 || i >= n)
            throw RqlException("invalid-plan",
                               "'" + label + "': index " + std::to_string(seg.index) + " out of range (count " + std::to_string(n) + ")");
        s.kind = Slot::ListIndex;
        s.index = seg.index;
        const TypeDescriptor* lt = container.list().type();
        s.type = lt ? lt->element : nullptr;
        s.nullable = lt ? lt->element_nullable : true;
        return s;
    }
    std::string type_name = type_name_of(container);
    std::string name;
    if (seg.kind == SegmentKind::Member) name = seg.name;
    else if (seg.kind == SegmentKind::Key && seg.key.type == Literal::Type::String) name = seg.key.s;
    else throw RqlException("invalid-plan", "'" + label + "': invalid segment " + seg.to_string() + " on " + type_name);
    const TypeDescriptor* t = (container.is_object() || container.is_struct()) ? container.type() : nullptr;
    const std::optional<TypeView> tv = t ? std::optional<TypeView>(x.view(t)) : std::nullopt;
    const MemberDescriptor* m = tv ? tv->find(name) : nullptr;  // hidden members are unwritable (spec §13)
    if (!m) {
        std::vector<std::string> names;
        if (tv)
            for (auto& mm : tv->members()) names.push_back(mm.name);
        auto close = suggest_closest(name, names, 3);
        throw RqlException("unknown-member", "no member '" + name + "' on " + type_name,
                           !close.empty() ? "did you mean " + join(close, " / ") + "?" : "members: " + join(names, " "));
    }
    s.kind = Slot::Member;
    s.member = m;
    s.owner = t;
    s.id = tv->id();
    s.type = m->type;
    s.nullable = m->nullable;
    return s;
}

Value safe_get(const Slot& slot, const Value& container, const std::string& label) {
    try {
        return slot.get(container);
    } catch (const std::exception& ex) {
        throw RqlException("getter-threw", "reading '" + label + "' failed: " + ex.what());
    }
}

/// Member slots must be writable and allowed by policy; list/map slots were checked by slot_of.
void check_writable(Exec& x, const Slot& slot, const std::string& label, const Value& root, const std::string& what) {
    if (slot.kind != Slot::Member) return;
    if (!member_writable(*slot.member, slot.id))
        throw RqlException("readonly-member", "'" + label + "' on " + x.ref_of(root) + " is not writable" + what + " (read-only member)");
    auto& policy = x.engine.mutations().can_write;
    if (policy && !policy(*slot.owner, *slot.member))
        throw RqlException("permission-denied", "'" + label + "' on " + x.ref_of(root) + " is not writable" + what + " (denied by host policy)");
}

Slot resolve(Exec& x, const Value& root, const std::vector<Segment>& segs, Value& container) {
    std::string label = raw_label(segs);
    Value cur = root;
    std::vector<std::pair<Slot, Value>> chain;
    for (size_t i = 0; i < segs.size(); ++i) {
        Slot slot = slot_of(x, cur, segs[i], label);
        if (i + 1 == segs.size()) {
            // Every value-type copy on the way is written back into its parent: those parent slots
            // must be writable and allowed too.
            for (size_t j = chain.size(); j-- > 0;) {
                if (!chain[j].second.is_struct()) break;
                check_writable(x, chain[j].first, label, root, ": '" + segs[j].to_string() + "' is a value type held by a non-writable member");
            }
            container = cur;
            return slot;
        }
        Value next = safe_get(slot, cur, label);
        if (next.is_null() || next.is_missing())
            throw RqlException("coercion-failed", "cannot set '" + label + "': '" + segs[i].to_string() + "' is null on " + x.ref_of(root));
        chain.emplace_back(slot, next);
        cur = next;
    }
    throw RqlException("invalid-plan", "empty set target");
}

/// Navigate fresh, write, then write back modified struct copies up to the first reference object.
void assign(Exec& x, const Value& root, const std::vector<Segment>& segs, const Value& value) {
    std::string label = raw_label(segs);
    std::vector<Value> containers{root};
    std::vector<Slot> slots;
    Value cur = root;
    for (size_t i = 0; i < segs.size(); ++i) {
        Slot slot = slot_of(x, cur, segs[i], label);
        slots.push_back(slot);
        if (i + 1 == segs.size()) break;
        cur = slot.get(cur);
        containers.push_back(cur);
    }
    slots.back().set(containers.back(), value);
    for (size_t j = containers.size() - 1; j >= 1; --j) {
        if (!containers[j].is_struct()) break;
        slots[j - 1].set(containers[j - 1], containers[j]);
    }
}

struct PlannedWrite {
    Value item;
    const std::vector<Segment>* segments;
    std::string label;
    Value old_value;
    Value new_value;
};

}  // namespace

std::optional<Value> coerce(Exec& x, const Value& v, const TypeDescriptor* target, bool nullable, std::string& why) {
    if (v.is_absent()) {
        why = "value is " + describe_value(v);
        return std::nullopt;
    }
    if (v.is_null()) {
        if (nullable || !target || (target->kind == TypeKind::Scalar && target->scalar == ScalarKind::Any)) return Value::null();
        why = "null is not allowed for " + target->friendly_name();
        return std::nullopt;
    }
    if (!target || (target->kind == TypeKind::Scalar && target->scalar == ScalarKind::Any)) return v;
    // Values already of the target type bypass adapters.
    if (is_instance_of(v, target)) return v;
    const bool nominal = target->kind == TypeKind::Struct || target->kind == TypeKind::Object || target->kind == TypeKind::Enum;
    for (const TypeAdapter* ad : x.adapters_for(target, Capability::Coerce)) {
        Value adapted;
        std::string reason;
        switch (ad->coerce(v, *target, adapted, reason)) {
            case AdapterOutcome::Ok:
                if (nominal && !adapted.is_null() && !is_instance_of(adapted, target))
                    throw std::logic_error("adapter " + ad->name + " coerced to " + type_name_of(adapted) + ", not " + target->name);
                return adapted;
            case AdapterOutcome::Failed:
                why = reason.empty() ? "rejected by " + ad->name : reason;
                return std::nullopt;
            case AdapterOutcome::NotHandled: break;
        }
    }
    switch (target->kind) {
        case TypeKind::Scalar:
            switch (target->scalar) {
                case ScalarKind::Bool:
                    if (v.is_bool()) return v;
                    why = "expected true or false";
                    return std::nullopt;
                case ScalarKind::String:
                    if (v.is_string()) return v;
                    why = "expected a string";
                    return std::nullopt;
                case ScalarKind::Integer:
                case ScalarKind::Single:
                case ScalarKind::Double: {
                    double d;
                    if (!try_number(v, d)) {
                        why = "expected a number";
                        return std::nullopt;
                    }
                    if (target->scalar == ScalarKind::Integer) {
                        if (std::floor(d) != d) {
                            why = "expected an integer";
                            return std::nullopt;
                        }
                        if (d < target->int_min || d > target->int_max) {
                            why = "out of range for " + target->friendly_name();
                            return std::nullopt;
                        }
                    }
                    if (target->scalar == ScalarKind::Single) d = static_cast<float>(d);
                    return Value::number(d, target);
                }
                default: return v;
            }
        case TypeKind::Enum: {
            int64_t e = 0;
            if (v.is_string() && target->enum_parse(v.as_string(), e) && target->enum_defined(e)) return Value::enumeration(target, e);
            double d;
            if (try_number(v, d) && std::floor(d) == d && target->enum_defined(static_cast<int64_t>(d)))
                return Value::enumeration(target, static_cast<int64_t>(d));
            why = "expected one of " + target->enum_names();
            return std::nullopt;
        }
        case TypeKind::Object:
        case TypeKind::Struct:  // instances returned above
            why = "expected " + target->friendly_name();
            return std::nullopt;
        case TypeKind::List: {
            if (!v.is_list()) {
                why = "expected " + target->friendly_name();
                return std::nullopt;
            }
            std::vector<Value> items;
            auto& l = v.list();
            size_t n = l.size();
            for (size_t i = 0; i < n; ++i) {
                std::string w;
                auto c = coerce(x, l.at(i), target->element, target->element_nullable, w);
                if (!c) {
                    why = "element: " + w;
                    return std::nullopt;
                }
                items.push_back(*c);
            }
            return Value::list_of(std::move(items));
        }
        case TypeKind::Map: {
            if (!v.is_map()) {
                why = "expected " + target->friendly_name();
                return std::nullopt;
            }
            std::vector<std::pair<Value, Value>> entries;
            bool ok = true;
            v.map().for_each([&](const Value& k, const Value& val) {
                std::string w;
                auto c = coerce(x, val, target->element, target->element_nullable, w);
                if (!c) {
                    why = "value: " + w;
                    ok = false;
                    return false;
                }
                entries.emplace_back(k, *c);
                return true;
            });
            if (!ok) return std::nullopt;
            return Value::map(std::make_shared<OwnedMap>(target, std::move(entries)));
        }
    }
    why = "expected " + target->friendly_name();
    return std::nullopt;
}

QueryResult run_set(Exec& x, Compiler& c, Encoder& enc, Stream& stream, const Stage& s) {
    check_policy(x, "set");
    struct Assign {
        const Expr* target;
        Eval value;
        std::string label;
    };
    std::vector<Assign> assigns;
    for (auto& a : s.assign) assigns.push_back({a.target.get(), c.compile(*a.value), label_of(*a.target)});
    std::vector<Value> items = targets(x, stream, "set");
    std::vector<PlannedWrite> writes;
    for (auto& item : items) {
        for (auto& a : assigns) {
            x.current = item;
            x.current_path = nullptr;
            x.current_depth = -1;
            Value nv = a.value(x);
            if (nv.is_missing() || nv.is_unknown())
                throw RqlException("coercion-failed", "value for '" + a.label + "' on " + x.ref_of(item) + " is missing");
            if (nv.is_error())
                throw RqlException(nv.error_code() == "stale-ref" ? "stale-ref" : "coercion-failed",
                                   "value for '" + a.label + "' on " + x.ref_of(item) + " failed: " +
                                       (nv.error_detail().empty() ? nv.error_code() : nv.error_detail()));
            Value container;
            Slot slot = resolve(x, item, a.target->segments, container);
            check_writable(x, slot, a.label, item, "");
            std::string why;
            auto coerced = coerce(x, nv, slot.type, slot.nullable, why);
            if (!coerced)
                throw RqlException("coercion-failed", "cannot assign " + describe_value(nv) + " to '" + a.label + "' (" +
                                                          (slot.type ? slot.type->friendly_name() : std::string("object")) + "): " + why);
            if (slot.kind == Slot::Member && slot.member->validate) {
                std::string rejected = slot.member->validate(container.ptr(), *coerced);  // before any write (spec §13)
                if (!rejected.empty())
                    throw RqlException("coercion-failed", "cannot assign " + describe_value(nv) + " to '" + a.label + "' on " +
                                                              x.ref_of(item) + ": " + rejected);
            }
            writes.push_back({item, &a.target->segments, a.label, snapshot(safe_get(slot, container, a.label)), *coerced});
        }
    }

    if (!x.options.dry_run) {
        size_t applied = 0;
        try {
            for (; applied < writes.size(); ++applied) assign(x, writes[applied].item, *writes[applied].segments, writes[applied].new_value);
        } catch (const std::exception& ex) {
            size_t rollback_failures = 0;
            for (size_t k = applied; k-- > 0;) {
                try {
                    assign(x, writes[k].item, *writes[k].segments, writes[k].old_value);
                } catch (...) {
                    ++rollback_failures;
                }
            }
            std::string rolled = rollback_failures == 0
                                     ? std::to_string(applied) + " earlier write(s) were rolled back"
                                     : "rolling back " + std::to_string(applied) + " earlier write(s) FAILED for " +
                                           std::to_string(rollback_failures) + " of them - state may be partially modified";
            throw RqlException("coercion-failed", "write '" + writes[applied].label + "' failed (" + ex.what() + "); " + rolled);
        }
    }

    std::vector<std::vector<Value>> rows;
    for (auto& w : writes) rows.push_back({Value::string(x.ref_of(w.item)), Value::string(w.label), w.old_value, w.new_value});
    QueryResult r = enc.rows({"$ref", "member", "old", "new"}, rows);
    r.dry_run = x.options.dry_run;
    return r;
}

QueryResult run_call(Exec& x, Compiler& c, Encoder& enc, Stream& stream, const Stage& s) {
    check_policy(x, "call");
    std::vector<Eval> args;
    for (auto& a : s.args) args.push_back(c.compile(*a));
    std::vector<Value> items = targets(x, stream, "call");
    struct Planned {
        Value item;
        const MethodDescriptor* method;
        std::vector<Value> args;
    };
    std::vector<Planned> planned;
    for (auto& item : items) {
        const TypeDescriptor* t = (item.is_object() || item.is_struct()) ? item.type() : nullptr;
        std::vector<const MethodDescriptor*> candidates;
        bool exists = false;
        std::vector<std::string> sigs;
        if (t) {
            for (auto& m : t->methods) {
                if (!iequals(m.name, s.method)) {
                    if (m.callable) sigs.push_back(t->signature(m));
                    continue;
                }
                exists = true;
                if (m.callable) candidates.push_back(&m);
            }
            for (auto* m : candidates) sigs.push_back(t->signature(*m));
        }
        std::string tn = type_name_of(item);
        if (candidates.empty())
            throw RqlException(exists ? "permission-denied" : "unknown-function", "no callable method '" + s.method + "' on " + tn,
                               !sigs.empty() ? "callable: " + join(sigs, ", ") : tn + " has no callable methods");
        x.current = item;
        x.current_depth = -1;
        std::vector<Value> vals;
        for (auto& a : args) vals.push_back(a(x));
        const MethodDescriptor* chosen = nullptr;
        std::vector<Value> coerced;
        std::string why;
        for (auto* m : candidates) {
            const auto& ps = m->params;
            bool fits = ps.size() >= vals.size();
            for (size_t i = vals.size(); fits && i < ps.size(); ++i)
                if (!ps[i].has_default) fits = false;
            if (!fits) {
                why = "expects " + std::to_string(ps.size()) + " argument(s)";
                continue;
            }
            std::vector<Value> cv;
            bool ok = true;
            for (size_t i = 0; i < ps.size(); ++i) {
                if (i >= vals.size()) {
                    cv.push_back(ps[i].default_value);
                    continue;
                }
                std::string w;
                auto r = coerce(x, vals[i], ps[i].type, ps[i].nullable, w);
                if (!r) {
                    ok = false;
                    why = "argument '" + ps[i].name + "': " + w;
                    break;
                }
                cv.push_back(*r);
            }
            if (ok) {
                chosen = m;
                coerced = std::move(cv);
                break;
            }
        }
        if (!chosen) {
            std::vector<std::string> cs;
            for (auto* m : candidates) cs.push_back(t->signature(*m));
            throw RqlException("coercion-failed", "cannot call " + s.method + " on " + x.ref_of(item) + ": " + why,
                               "signature: " + join(cs, ", "));
        }
        planned.push_back({item, chosen, std::move(coerced)});
    }

    std::vector<std::vector<Value>> rows;
    size_t done = 0;
    for (auto& p : planned) {
        Value result = Value::string("(dry-run)");
        if (!x.options.dry_run) {
            try {
                result = p.method->invoke(p.item.ptr(), p.args);
            } catch (const std::exception& ex) {
                throw RqlException("getter-threw", p.method->name + " on " + x.ref_of(p.item) + " threw: " + ex.what() + "; " +
                                                       std::to_string(done) + " earlier call(s) completed and are NOT rolled back");
            }
        }
        ++done;
        rows.push_back({Value::string(x.ref_of(p.item)), Value::string(p.item.type()->signature(*p.method)), result});
    }
    QueryResult r = enc.rows({"$ref", "method", "result"}, rows);
    r.dry_run = x.options.dry_run;
    return r;
}

}  // namespace detail
}  // namespace rql
