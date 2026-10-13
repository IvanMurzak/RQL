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
    int Id = 0;  // read-only for RQL (and the identity member)
    std::string Name;
    NodeKind Kind = NodeKind::Alpha;
    double Value = 0;
    bool Enabled = false;
    std::optional<std::string> Note;
    Vec2 Pos;
    std::vector<std::string> Tags;
    std::map<std::string, double> Props;
    std::vector<std::unique_ptr<Node>> Children;
    Node* Parent = nullptr;  // reference (non-owning)
    Node* Peer = nullptr;    // reference (non-owning)
};

struct Special : Node {
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
std::unique_ptr<Graph> load_graph(const std::string& graph_json_path);
std::string read_file(const std::string& path);

}  // namespace conformance
