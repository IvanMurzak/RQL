#include "rql/engine.hpp"

#include <cmath>
#include <unordered_set>

#include "exec.hpp"

namespace rql {

namespace {

bool ident_start(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_'; }

void validate_name(const std::string& name, bool require_namespace) {
    if (name.empty()) throw std::invalid_argument("name required");
    size_t start = 0;
    for (;;) {
        size_t dot = name.find('.', start);
        std::string part = name.substr(start, dot == std::string::npos ? std::string::npos : dot - start);
        if (part.empty() || !ident_start(part[0])) throw std::invalid_argument("invalid name '" + name + "'");
        if (dot == std::string::npos) break;
        start = dot + 1;
    }
    if (require_namespace && name.find('.') == std::string::npos)
        throw std::invalid_argument("extension names must be namespaced (ns.name): '" + name + "'");
}

const char* kCoreStageNames[] = {"where", "select", "sort", "first", "skip", "count", "describe", "get", "sum",
                                 "min", "max", "avg", "group-by", "distinct", "recurse", "set", "call"};
const char* kFunctionNames[] = {"has", "is-null", "len", "lower", "upper", "abs", "round", "floor", "ceil", "coalesce", "ref",
                                "contains", "starts-with", "ends-with", "matches"};
const char* kMethodNames[] = {"any", "all", "count", "sum", "min", "max", "avg", "first", "keys", "values",
                              "contains", "starts-with", "ends-with", "matches", "lower", "upper", "len"};

}  // namespace

Engine& Engine::add_source(const std::string& name, std::function<Value()> getter, std::string description) {
    validate_name(name, false);
    for (auto& s : sources_) {
        if (s.name == name) {
            s.getter = std::move(getter);
            s.description = std::move(description);
            return *this;
        }
    }
    sources_.push_back({name, std::move(getter), std::move(description)});
    return *this;
}

Engine& Engine::add_function(const std::string& name, FunctionHandler fn) {
    validate_name(name, true);
    functions_[name] = std::move(fn);
    return *this;
}

Engine& Engine::add_stage(const std::string& name, StageHandler handler) {
    validate_name(name, true);
    stages_[name] = std::move(handler);
    return *this;
}

const SourceRegistration* Engine::find_source(const std::string& name) const {
    for (auto& s : sources_)
        if (s.name == name) return &s;
    const SourceRegistration* hit = nullptr;
    for (auto& s : sources_) {
        if (iequals(s.name, name)) {
            if (hit) return nullptr;
            hit = &s;
        }
    }
    return hit;
}

// ------------------------------------------------------------------ execution

QueryResult Engine::execute(const QueryPlan& plan, const Budgets& budgets, const QueryOptions& options) {
    detail::Exec x(*this, budgets, options, plan);
    QueryResult r = detail::run_pipeline(x);
    r.stats.visited = x.visited;
    r.stats.members_read = x.members_read;
    r.stats.elapsed_ms = x.elapsed_ms();
    return r;
}

QueryResult Engine::execute_json(const std::string& plan_json, const Budgets& budgets, const QueryOptions& options) {
    QueryPlan plan;
    try {
        plan = decode_plan(std::string_view(plan_json));
    } catch (const RqlException& ex) {
        return QueryResult::fail({Diagnostic::from(ex)});
    }
    return execute(plan, budgets, options);
}

QueryResult Engine::execute_request(const Json& req) {
    try {
        if (!req.is_object()) throw RqlException("invalid-plan", "request must be an object");
        for (size_t i = 0; i < req.size(); ++i) {
            const std::string& k = req.key(i);
            if (k != "rql" && k != "plan" && k != "text" && k != "budgets" && k != "options")
                throw RqlException("invalid-plan", "unexpected property '" + k + "' in request");
        }
        if (const Json* v = req.find("rql")) {
            if (!v->is_string() || v->as_string() != kRqlVersion)
                throw RqlException("version-unsupported", "request version is not supported (supported: " + std::string(kRqlVersion) + ")");
        }
        const Json* plan = req.find("plan");
        const Json* text = req.find("text");
        if ((plan != nullptr) == (text != nullptr)) throw RqlException("invalid-plan", "request needs exactly one of 'plan' or 'text'");
        if (text)
            throw RqlException("unsupported", "the C++ runtime executes QueryPlans only; parse RQL text with the .NET/TypeScript parser sidecar");
        Budgets b;
        QueryOptions o;
        if (const Json* bj = req.find("budgets")) b = Budgets::from_json(*bj);
        if (const Json* oj = req.find("options")) o = QueryOptions::from_json(*oj);
        QueryPlan p = decode_plan(*plan);
        return execute(p, b, o);
    } catch (const RqlException& ex) {
        return QueryResult::fail({Diagnostic::from(ex)});
    }
}

// ------------------------------------------------------------------ references

int64_t Engine::handle_of(const Value& v) {
    std::lock_guard<std::mutex> lock(handle_mutex_);
    const void *a, *b;
    if (!v.identity(a, b)) {
        int64_t id = ++next_handle_;
        return id;
    }
    HandleKey key{a, b};
    auto it = handles_.find(key);
    if (it != handles_.end()) return it->second;
    int64_t id = ++next_handle_;
    handles_.emplace(key, id);
    // Weak semantics: keep only the raw identity (no keep-alive), re-validated on resolution.
    handle_targets_.emplace(id, HandleTarget{v.is_object() ? Value::object(v.type(), v.ptr()) : Value::missing()});
    return id;
}

void Engine::clear_session_handles() {
    std::lock_guard<std::mutex> lock(handle_mutex_);
    handles_.clear();
    handle_targets_.clear();
}

std::string Engine::ref_of(const Value& v) {
    if (v.is_object()) {
        const TypeDescriptor* t = v.type();
        if (const MemberDescriptor* idm = t->id_member()) {
            Value id;
            try {
                id = idm->getter(v.ptr());
            } catch (const std::exception&) {
                id = Value::null();
            }
            if (!id.is_null() && !id.is_absent()) return t->name + "#" + to_text(id);
        }
        return t->name + "@" + std::to_string(handle_of(v));
    }
    return type_name_of(v) + "@" + std::to_string(handle_of(v));
}

Value Engine::search_sources(const std::string& type, const std::string& id, const Value* identity) {
    struct IdKey {
        const void* a;
        const void* b;
        bool operator==(const IdKey& o) const { return a == o.a && b == o.b; }
    };
    struct IdHash {
        size_t operator()(const IdKey& k) const { return std::hash<const void*>()(k.a) * 31u ^ std::hash<const void*>()(k.b); }
    };
    const void *ta = nullptr, *tb = nullptr;
    if (identity) identity->identity(ta, tb);
    std::unordered_set<IdKey, IdHash> visited;
    std::vector<std::pair<Value, int>> stack;
    for (auto& s : sources_) {
        Value v;
        try {
            v = s.getter();
        } catch (const std::exception&) {
            continue;
        }
        if (!v.is_null() && !v.is_absent()) stack.emplace_back(v, 0);
    }
    int budget = defaults_.max_visited;
    while (!stack.empty() && budget-- > 0) {
        auto [o, depth] = stack.back();
        stack.pop_back();
        const void *a, *b;
        if (!o.identity(a, b) || !visited.insert(IdKey{a, b}).second) continue;
        if (o.is_list() || o.is_map()) {
            std::vector<Value> els;
            detail::elements(o, els);
            for (auto& e : els) {
                Value ev = e.is_entry() ? e.entry().value : e;
                if (ev.is_object() || ev.is_list() || ev.is_map()) stack.emplace_back(ev, depth);
            }
            continue;
        }
        if (!o.is_object()) continue;
        const TypeDescriptor* t = o.type();
        if (identity) {
            if (a == ta && b == tb) return o;
        } else if (const MemberDescriptor* idm = t->id_member()) {
            if (t->is_a(type)) {
                Value v;
                try {
                    v = idm->getter(o.ptr());
                } catch (const std::exception&) {
                    v = Value::null();
                }
                if (!v.is_null() && !v.is_absent() && to_text(v) == id) return o;
            }
        }
        if (depth >= 32) continue;
        for (auto& m : t->members) {
            if (!m.owning || !m.type) continue;
            if (m.type->kind == TypeKind::Enum || m.type->kind == TypeKind::Struct) continue;
            if (m.type->kind == TypeKind::Scalar && m.type->scalar != ScalarKind::Any) continue;
            Value v;
            try {
                v = m.getter(o.ptr());
            } catch (const std::exception&) {
                continue;
            }
            if (v.is_object() || v.is_list() || v.is_map()) stack.emplace_back(v, depth + 1);
        }
    }
    return Value::missing();
}

Value Engine::resolve_ref(const std::string& reference_in, std::string& error) {
    std::string reference = reference_in;
    while (!reference.empty() && (reference.front() == ' ' || reference.front() == '\t')) reference.erase(reference.begin());
    while (!reference.empty() && (reference.back() == ' ' || reference.back() == '\t')) reference.pop_back();
    // Tolerate copied display forms such as "Enemy#12(Goblin_0012)" or "Enemy#12 Goblin".
    size_t cut = reference.find_first_of(" (\t");
    if (cut != std::string::npos && cut > 0) reference = reference.substr(0, cut);
    size_t i = reference.find_first_of("#@");
    if (i == std::string::npos || i == 0 || i == reference.size() - 1) {
        error = "malformed reference '" + reference + "' (expected Type#id or Type@handle)";
        return Value::missing();
    }
    std::string type = reference.substr(0, i);
    std::string id = reference.substr(i + 1);
    Value found;
    if (reference[i] == '@') {
        double h = 0;
        Value candidate;
        if (parse_double(id, h) && std::floor(h) == h) {
            std::lock_guard<std::mutex> lock(handle_mutex_);
            auto it = handle_targets_.find(static_cast<int64_t>(h));
            if (it != handle_targets_.end()) candidate = it->second.value;
        }
        if (candidate.is_object()) {
            // Without a liveness hook, only hand out objects that are still reachable from a source.
            found = is_alive ? candidate : search_sources(type, id, &candidate);
        }
    } else {
        found = resolve_stable_ref ? resolve_stable_ref(type, id) : search_sources(type, id, nullptr);
    }
    if (!found.is_object() || !found.type()->is_a(type)) {
        error = "reference '" + reference + "' does not resolve to a live object";
        return Value::missing();
    }
    if (is_alive && !is_alive(found)) {
        error = "reference '" + reference + "' points to a destroyed object";
        return Value::missing();
    }
    return found;
}

// ------------------------------------------------------------------ schema

Json Engine::describe_type(const TypeDescriptor& t) const {
    Json o = Json::object();
    o.set("name", t.name);
    if (t.base) o.set("base", t.base->name);
    o.set("kind", t.kind == TypeKind::Enum ? "enum" : t.kind == TypeKind::Struct ? "struct" : "object");
    if (!t.description.empty()) o.set("description", t.description);
    if (t.kind == TypeKind::Enum) {
        Json values = Json::array();
        for (auto& e : t.enum_values) values.push(Json(e.first));
        o.set("values", std::move(values));
        o.set("members", Json::array());
        return o;
    }
    Json members = Json::array();
    for (auto& m : t.members) {
        Json mo = Json::object();
        mo.set("name", m.name);
        std::string tn = m.type ? m.type->friendly_name() : "object";
        if (m.nullable && m.type && m.type->is_value_type() && m.type->scalar != ScalarKind::String) tn += "?";
        mo.set("type", tn);
        if (mutations_.enabled && t.member_writable(m)) mo.set("writable", true);
        if (!m.description.empty()) mo.set("description", m.description);
        members.push(std::move(mo));
    }
    o.set("members", std::move(members));
    Json methods = Json::array();
    for (auto& m : t.methods)
        if (m.callable) methods.push(Json(t.signature(m)));
    if (methods.size() > 0) o.set("methods", std::move(methods));
    return o;
}

Json Engine::capabilities() {
    Json o = Json::object();
    o.set("rql", kRqlVersion);
    o.set("runtime", runtime_name);
    Json sources = Json::array();
    for (auto& s : sources_) {
        Json so = Json::object();
        so.set("name", s.name);
        std::string type = "object";
        try {
            Value v = s.getter();
            type = friendly_type_of(v);
        } catch (const std::exception&) {
        }
        so.set("type", type);
        if (!s.description.empty()) so.set("description", s.description);
        sources.push(std::move(so));
    }
    o.set("sources", std::move(sources));
    Json stages = Json::array();
    for (auto* s : kCoreStageNames) stages.push(Json(s));
    for (auto& kv : stages_) stages.push(Json(kv.first));
    o.set("stages", std::move(stages));
    Json functions = Json::array();
    for (auto* f : kFunctionNames) functions.push(Json(f));
    for (auto& kv : functions_) functions.push(Json(kv.first));
    o.set("functions", std::move(functions));
    Json methods = Json::array();
    for (auto* m : kMethodNames) methods.push(Json(m));
    o.set("methods", std::move(methods));
    Json mut = Json::object();
    mut.set("enabled", mutations_.enabled);
    mut.set("maxTargets", mutations_.max_targets);
    Json callable = Json::array();
    for (auto* t : types_->registered())
        for (auto& m : t->methods)
            if (m.callable) callable.push(Json(t->name + "." + t->signature(m)));
    mut.set("callable", std::move(callable));
    o.set("mutations", std::move(mut));
    Json limits = Json::object();
    limits.set("maxVisited", defaults_.hard_max_visited);
    limits.set("maxRows", defaults_.hard_max_rows);
    limits.set("maxListItems", defaults_.max_list_items);
    limits.set("maxDepth", defaults_.max_depth);
    limits.set("timeoutMs", defaults_.hard_timeout_ms);
    limits.set("maxOutputBytes", defaults_.max_output_bytes);
    limits.set("maxMutationTargets", mutations_.max_targets);
    o.set("limits", std::move(limits));
    Json types = Json::array();
    for (auto* t : types_->registered()) types.push(describe_type(*t));
    o.set("types", std::move(types));
    return o;
}

}  // namespace rql
