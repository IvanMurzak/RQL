#include "conformance_graph.hpp"

#include <cmath>
#include <fstream>
#include <regex>
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

namespace {

std::string ascii_case(std::string s, bool upper) {
    for (auto& c : s) {
        if (upper && c >= 'a' && c <= 'z') c = static_cast<char>(c - 32);
        else if (!upper && c >= 'A' && c <= 'Z') c = static_cast<char>(c + 32);
    }
    return s;
}

double length(const Vec2& v) { return std::sqrt(v.x * v.x + v.y * v.y); }

bool number_text(const std::string& t, double& out) {
    return rql::parse_double(!t.empty() && t[0] == '+' ? t.substr(1) : t, out);
}

bool try_vec(rql::TypeRegistry& reg, const rql::Value& v, Vec2& out) {
    if (const Vec2* already = rql::value_as<Vec2>(reg, v)) {
        out = *already;
        return true;
    }
    if (v.is_string()) {
        static const std::regex text(R"(^\(\s*([-+0-9.eE]+)\s*,\s*([-+0-9.eE]+)\s*\)$)");
        std::smatch m;
        if (!std::regex_match(v.as_string(), m, text)) return false;
        return number_text(m[1].str(), out.x) && number_text(m[2].str(), out.y);
    }
    if (v.is_list() && v.list().size() == 2) {
        rql::Value a = v.list().at(0), b = v.list().at(1);
        if (!a.is_number() || !b.is_number()) return false;
        out = Vec2{a.as_number(), b.as_number()};
        return true;
    }
    return false;
}

}  // namespace

void register_test_adapters(rql::Engine& engine, rql::TypeRegistry& reg) {
    rql::TypeRegistry* r = &reg;

    rql::TypeAdapter vec;
    vec.name = "Vec2Adapter";
    vec.target = reg.descriptor<Vec2>();
    vec.description = "Vec2: write as \"(x, y)\" or [x, y]";
    vec.configure_members = [](rql::MemberTable& t) {
        t.computed<Vec2>("len", [](Vec2& v) { return length(v); }, "vector length sqrt(x*x+y*y)");
    };
    vec.encode = [r](const rql::Value& v, const rql::EncodeContext&) -> std::optional<rql::Json> {
        const Vec2* p = rql::value_as<Vec2>(*r, v);
        if (!p) return std::nullopt;
        return rql::Json("(" + rql::format_number_text(p->x) + ", " + rql::format_number_text(p->y) + ")");
    };
    vec.coerce = [r](const rql::Value& v, const rql::TypeDescriptor&, rql::Value& result, std::string& reason) {
        Vec2 out;
        if (try_vec(*r, v, out)) {
            result = rql::Traits<Vec2>::get(*r, out);
            return rql::AdapterOutcome::Ok;
        }
        reason = "expected (x, y) or [x, y]";
        return rql::AdapterOutcome::Failed;
    };
    vec.equals = [r](const rql::Value& v, const rql::Value& other) -> std::optional<bool> {
        const Vec2* p = rql::value_as<Vec2>(*r, v);
        Vec2 o;
        if (!p || !try_vec(*r, other, o)) return std::nullopt;
        return std::fabs(p->x - o.x) <= 1e-9 && std::fabs(p->y - o.y) <= 1e-9;
    };
    vec.compare = [r](const rql::Value& v, const rql::Value& other) -> std::optional<int> {
        const Vec2* p = rql::value_as<Vec2>(*r, v);
        Vec2 o;
        if (!p || !try_vec(*r, other, o)) return std::nullopt;
        double a = length(*p), b = length(o);
        return a < b ? -1 : (a > b ? 1 : 0);
    };
    engine.add_adapter(std::move(vec));

    rql::TypeAdapter node;
    node.name = "NodeAdapter";
    node.target = reg.descriptor<Node>();
    node.description = "Node: a conformance graph node";
    node.configure_members = [](rql::MemberTable& t) {
        t.computed<Node>("childCount", [](Node& n) { return static_cast<int>(n.Children.size()); }, "number of direct children")
            .property<Node>(
                "title", [](Node& n) { return ascii_case(n.Name, true); },
                [](Node& n, std::string v) {
                    if (n.Id == 3) throw std::runtime_error("node 3 is locked");
                    n.Name = ascii_case(v, false);
                },
                [](Node&, const std::string& v) { return v.empty() ? std::string("title must not be empty") : std::string(); },
                "upper-cased Name; assigning sets Name lower-cased")
            .hide({"Note"})
            .explicit_only({"Children"});
    };
    engine.add_adapter(std::move(node));

    rql::TypeAdapter special;
    special.name = "SpecialAdapter";
    special.target = reg.descriptor<Special>();
    special.configure_members = [](rql::MemberTable& t) {
        t.computed<Special>("powerLevel", [](Special& s) { return s.Power / 1000.0; }, "Power / 1000");
    };
    engine.add_adapter(std::move(special));
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
        for (auto& c : req(e, "Children").items()) n->Children.push_back(std::shared_ptr<Node>(build(c)));
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
