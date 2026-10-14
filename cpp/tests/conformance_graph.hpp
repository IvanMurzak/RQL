// Native C++ mirror of fixtures/cross-runtime/graph.json "types" (declaration order matters),
// registered with the provider-based reflection, plus a loader that materializes graph.json.
#pragma once

#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "rql/rql.hpp"

namespace conformance {

enum class NodeKind { Alpha, Beta, Gamma };

struct Vec2 {
    double x = 0;
    double y = 0;
};

struct Node {
    virtual ~Node() = default;
    /// RTTI-free dynamic type for the provider (auto-detected by rql::TypeRegistry::wrap).
    virtual rql::TypeKey rql_dynamic_type() const { return rql::type_key<Node>(); }
    int Id = 0;  // read-only for RQL (and the identity member)
    std::string Name;
    NodeKind Kind = NodeKind::Alpha;
    double Value = 0;
    bool Enabled = false;
    std::optional<std::string> Note;
    Vec2 Pos;
    std::vector<std::string> Tags;
    std::map<std::string, double> Props;
    // shared_ptr (not unique_ptr): the member is assignable, as in the .NET reference (List<Node> has a setter).
    std::vector<std::shared_ptr<Node>> Children;
    Node* Parent = nullptr;  // reference (non-owning)
    Node* Peer = nullptr;    // reference (non-owning)
};

struct Special : Node {
    rql::TypeKey rql_dynamic_type() const override { return rql::type_key<Special>(); }
    int Power = 0;
};

struct Graph {
    std::string Name;
    int Version = 0;
    std::vector<std::unique_ptr<Node>> Nodes;
    std::map<std::string, Node*> Index;  // reference (non-owning)
    std::vector<std::unique_ptr<Node>> Empty;
    Node* Nothing = nullptr;             // reference (non-owning)
};

void register_types(rql::TypeRegistry& reg);
/// The spec §13 test adapter set (fixtures/adapters/cases.json "adapters").
void register_test_adapters(rql::Engine& engine, rql::TypeRegistry& reg);
std::unique_ptr<Graph> load_graph(const std::string& graph_json_path);
std::string read_file(const std::string& path);

}  // namespace conformance
