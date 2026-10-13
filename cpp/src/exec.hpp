// Internal execution state shared by compiler / pipeline / encoder / mutator.
#pragma once

#include <chrono>
#include <functional>
#include <memory>
#include <optional>
#include <regex>
#include <string>
#include <unordered_map>
#include <vector>

#include "rql/engine.hpp"

namespace rql {
namespace detail {

struct PathNode {
    std::shared_ptr<const PathNode> parent;
    std::string text;
    std::string str() const;
};
using PathPtr = std::shared_ptr<const PathNode>;
PathPtr path_text(PathPtr parent, std::string text);
PathPtr path_member(const PathPtr& parent, const std::string& name);
PathPtr path_at(const PathPtr& parent, int64_t i);
PathPtr path_key(const PathPtr& parent, const Value& key);

struct Item {
    Value value;
    PathPtr path;
    int depth = -1;  // depth within the most recent recurse stage; -1 outside
};

/// Per-member-path-segment statistics (unknown-member warnings) + monomorphic inline cache.
struct MemberSite {
    std::string name;
    int64_t hits = 0;
    int64_t misses = 0;
    std::vector<const TypeDescriptor*> miss_types;
    std::vector<std::string> row_columns;  // columns of a multi-column row that lacked the member
    const TypeDescriptor* cached_type = nullptr;
    const MemberDescriptor* cached_member = nullptr;
    explicit MemberSite(std::string n) : name(std::move(n)) {}
};

/// Statistics of one `x is T` test (no-match warning).
struct IsSite {
    std::string type_name;
    int64_t evaluated = 0;
    int64_t matched = 0;
    std::vector<std::string> seen;
};

class Exec {
public:
    Exec(Engine& engine, const Budgets& budgets, const QueryOptions& options, const QueryPlan& plan);

    Engine& engine;
    TypeRegistry& types;
    QueryOptions options;
    const QueryPlan& plan;
    int max_visited, max_rows, max_list_items, max_depth, timeout_ms, max_output_bytes, max_mutation_targets;
    bool track_paths = false;

    // evaluation scope
    Value current;
    PathPtr current_path;
    int current_depth = -1;
    int64_t index = 0;
    std::vector<Value> outer;  // enclosing items while evaluating collection-method arguments

    int64_t visited = 0;
    int64_t members_read = 0;
    int getter_errors = 0;
    std::string first_getter_error;
    std::vector<Diagnostic> diagnostics;
    std::vector<std::unique_ptr<MemberSite>> sites;
    std::vector<std::unique_ptr<IsSite>> is_sites;
    std::unordered_map<std::string, std::shared_ptr<std::regex>> regex_cache;

    void visit();
    void check_time() const;
    void getter_threw(const std::string& member, const std::string& what);
    double elapsed_ms() const;

private:
    std::chrono::steady_clock::time_point start_;
};

using Eval = std::function<Value(Exec&)>;

// ---------------------------------------------------------------- value semantics (compiler.cpp)

/// Kleene truth: bool → value, null → false, otherwise unknown (nullopt).
std::optional<bool> truth(const Value& v);
Value tri(std::optional<bool> b);
std::optional<bool> rql_equal(const Value& a, const Value& b);
std::optional<int> rql_compare(const Value& a, const Value& b);
bool try_number(const Value& v, double& d, bool& single);
inline bool try_number(const Value& v, double& d) { bool s; return try_number(v, d, s); }
Value reduce(const std::vector<Value>& values, const std::string& kind);
bool distinct_equal(const Value& a, const Value& b);
size_t distinct_hash(const Value& v);
/// Elements of a list (values) or a map (entries); false when v is not a collection.
bool elements(const Value& v, std::vector<Value>& out);
size_t collection_size(const Value& v);
std::vector<std::string> suggest_closest(const std::string& name, const std::vector<std::string>& candidates, size_t max);
std::string join(const std::vector<std::string>& parts, const std::string& sep);
Value member_of(Exec& x, const Value& v, MemberSite& site);

class Compiler {
public:
    explicit Compiler(Exec& x) : x_(x) {}
    Eval compile(const Expr& e);

private:
    Exec& x_;
    Eval compile_path(const Expr& p);
    Eval compile_meta(const std::string& name, Eval prev, bool of_current_item);
    Eval compile_meta_core(const std::string& name, Eval prev, bool of_current_item);
    Eval compile_op(const Expr& o);
    Eval compile_op_args(const std::string& op, const ExprPtr& a, const ExprPtr& b);
    Eval compile_call(const Expr& c);
    Eval compile_function(const Expr& c);
    Eval compile_method(const Expr& c);
    Eval compile_extension(const FunctionHandler& fn, const std::vector<ExprPtr>& args);
    void arity(const Expr& c, size_t min, size_t max);
};

// ---------------------------------------------------------------- encoder (encoder.cpp)

class Encoder {
public:
    explicit Encoder(Exec& x) : x_(x) {}
    Json encode(const Value& v, int depth = 0);
    Json summary(const Value& obj);
    QueryResult rows(const std::vector<std::string>& columns, const std::vector<std::vector<Value>>& rows);
    QueryResult items(const std::vector<Value>& items);
    static Json number(double d, bool single);

private:
    Exec& x_;
    Json inline_object(const Value& obj, int depth);
};

// ---------------------------------------------------------------- streams (pipeline.cpp)

class Stream {
public:
    virtual ~Stream() = default;
    virtual bool next(Item& out) = 0;
};
using StreamPtr = std::unique_ptr<Stream>;

QueryResult run_pipeline(Exec& x);

// ---------------------------------------------------------------- mutations (mutator.cpp)

QueryResult run_set(Exec& x, Compiler& c, Encoder& enc, Stream& stream, const Stage& s);
QueryResult run_call(Exec& x, Compiler& c, Encoder& enc, Stream& stream, const Stage& s);
/// Coerce a value to a member/parameter type (spec §8); nullopt + why on failure.
std::optional<Value> coerce(const Value& v, const TypeDescriptor* target, bool nullable, std::string& why);

// ---------------------------------------------------------------- describe (encoder.cpp)
QueryResult run_describe(Exec& x, Stream& stream);

}  // namespace detail
}  // namespace rql
