// Host-facing RQL runtime over live C++ objects (mirrors dotnet/Rql.Execution/RqlEngine.cs).
#pragma once

#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "rql/codec.hpp"
#include "rql/plan.hpp"
#include "rql/reflection.hpp"
#include "rql/result.hpp"
#include "rql/value.hpp"

namespace rql {

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

struct MutationPolicy {
    /// Host-level switch. Requests must additionally set QueryOptions::allow_mutation.
    bool enabled = false;
    /// Optional extra allow-check per (declaring object type, member) → permission-denied when false.
    std::function<bool(const TypeDescriptor& owner, const MemberDescriptor& member)> can_write;
    int max_targets = 20;
};

struct EngineDefaults {
    int max_visited = 200000;
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
    explicit Engine(TypeRegistry& types) : types_(&types) {}
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

    Engine& add_source(const std::string& name, std::function<Value()> getter, std::string description = {});
    /// Convenience: a registered object exposed as a source.
    template <class T>
    Engine& add_object_source(const std::string& name, T* object, std::string description = {}) {
        TypeRegistry* r = types_;
        return add_source(name, [r, object]() { return r->wrap(object); }, std::move(description));
    }
    /// Namespaced extension function "ns.fn".
    Engine& add_function(const std::string& name, FunctionHandler fn);
    /// Namespaced extension stage "ns.stage".
    Engine& add_stage(const std::string& name, StageHandler handler);

    const std::vector<SourceRegistration>& sources() const { return sources_; }
    const SourceRegistration* find_source(const std::string& name) const;  // exact, then unique case-insensitive
    const std::map<std::string, FunctionHandler>& functions() const { return functions_; }
    const std::map<std::string, StageHandler>& stages() const { return stages_; }

    QueryResult execute(const QueryPlan& plan, const Budgets& budgets = {}, const QueryOptions& options = {});
    /// Decode + execute a plan JSON text; codec errors become an error result.
    QueryResult execute_json(const std::string& plan_json, const Budgets& budgets = {}, const QueryOptions& options = {});
    /// Transport envelope {"rql","plan","budgets","options"} (text requests need a parser sidecar → unsupported).
    QueryResult execute_request(const Json& request);

    /// Stable reference string: "Type#id" (id member) or session handle "Type@n".
    std::string ref_of(const Value& v);
    /// Resolve a reference string (copied display forms tolerated). On failure returns missing and sets error.
    Value resolve_ref(const std::string& reference, std::string& error);
    void clear_session_handles();

    /// Capability manifest (spec/capability-manifest.schema.json).
    Json capabilities();
    /// Type descriptor JSON (capability-manifest typeDescriptor).
    Json describe_type(const TypeDescriptor& t) const;

private:
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

    int64_t handle_of(const Value& v);
    Value search_sources(const std::string& type, const std::string& id, const Value* identity);
};

}  // namespace rql
