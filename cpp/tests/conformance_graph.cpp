#include "conformance_graph.hpp"

#include <fstream>
#include <sstream>
#include <stdexcept>
#include <unordered_map>

namespace conformance {

void register_types(rql::TypeRegistry& reg) {
    reg.structure<Vec2>("Vec2").member("x", &Vec2::x).member("y", &Vec2::y);
    reg.enumeration<NodeKind>("NodeKind").value("Alpha", NodeKind::Alpha).value("Beta", NodeKind::Beta).value("Gamma", NodeKind::Gamma);
    reg.object<Node>("Node")
        .member("Id", &Node::Id).readonly()
        .member("Name", &Node::Name)
        .member("Kind", &Node::Kind)
        .member("Value", &Node::Value)
        .member("Enabled", &Node::Enabled)
        .member("Note", &Node::Note)
        .member("Pos", &Node::Pos)
        .member("Tags", &Node::Tags)
        .member("Props", &Node::Props)
        .member("Children", &Node::Children)
        .member("Parent", &Node::Parent).reference()
        .member("Peer", &Node::Peer).reference()
        .id("Id")
        .display("Name");
    reg.object<Special>("Special").base<Node>().member("Power", &Special::Power);
    reg.object<Graph>("Graph")
        .member("Name", &Graph::Name)
        .member("Version", &Graph::Version)
        .member("Nodes", &Graph::Nodes)
        .member("Index", &Graph::Index).reference()
        .member("Empty", &Graph::Empty)
        .member("Nothing", &Graph::Nothing).reference();
}

std::string read_file(const std::string& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) throw std::runtime_error("cannot open " + path);
    std::ostringstream ss;
    ss << in.rdbuf();
    return ss.str();
}

namespace {

using rql::Json;

const Json& req(const Json& o, const char* k) {
    const Json* v = o.find(k);
    if (!v) throw std::runtime_error(std::string("graph.json: missing '") + k + "'");
    return *v;
}

struct Loader {
    std::unordered_map<int, Node*> by_id;
    std::vector<std::pair<const Json*, Node*>> links;

    std::unique_ptr<Node> build(const Json& e) {
        std::unique_ptr<Node> n;
        if (req(e, "$type").as_string() == "Special") {
            auto s = std::make_unique<Special>();
            s->Power = static_cast<int>(req(e, "Power").as_number());
            n = std::move(s);
        } else {
            n = std::make_unique<Node>();
        }
        n->Id = static_cast<int>(req(e, "Id").as_number());
        n->Name = req(e, "Name").as_string();
        const std::string& kind = req(e, "Kind").as_string();
        n->Kind = kind == "Alpha" ? NodeKind::Alpha : kind == "Beta" ? NodeKind::Beta : NodeKind::Gamma;
        n->Value = req(e, "Value").as_number();
        n->Enabled = req(e, "Enabled").as_bool();
        const Json& note = req(e, "Note");
        if (!note.is_null()) n->Note = note.as_string();
        const Json& pos = req(e, "Pos");
        n->Pos = Vec2{req(pos, "x").as_number(), req(pos, "y").as_number()};
        for (auto& t : req(e, "Tags").items()) n->Tags.push_back(t.as_string());
        const Json& props = req(e, "Props");
        for (size_t i = 0; i < props.size(); ++i) n->Props[props.key(i)] = props.at(i).as_number();
        for (auto& c : req(e, "Children").items()) n->Children.push_back(build(c));
        by_id[static_cast<int>(req(e, "$id").as_number())] = n.get();
        links.emplace_back(&e, n.get());
        return n;
    }

    Node* link(const Json& e) {
        if (e.is_null()) return nullptr;
        return by_id.at(static_cast<int>(req(e, "$link").as_number()));
    }
};

}  // namespace

std::unique_ptr<Graph> load_graph(const std::string& path) {
    Json doc = Json::parse(read_file(path));
    const Json& root = req(doc, "root");
    auto g = std::make_unique<Graph>();
    g->Name = req(root, "Name").as_string();
    g->Version = static_cast<int>(req(root, "Version").as_number());
    Loader l;
    for (auto& n : req(root, "Nodes").items()) g->Nodes.push_back(l.build(n));
    for (auto& [el, node] : l.links) {
        node->Parent = l.link(req(*el, "Parent"));
        node->Peer = l.link(req(*el, "Peer"));
    }
    const Json& index = req(root, "Index");
    for (size_t i = 0; i < index.size(); ++i) g->Index[index.key(i)] = l.link(index.at(i));
    g->Nothing = l.link(req(root, "Nothing"));
    return g;
}

}  // namespace conformance
