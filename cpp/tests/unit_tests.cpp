// Unit tests: JSON edge cases, number formatting, and engine features the golden vectors do not
// cover (call stage, extensions, session handles, liveness, unordered maps, shared_ptr, rollback).
#include <cmath>
#include <cstdlib>
#include <iostream>
#include <stdexcept>
#include <string>
#include <unordered_map>

#include "conformance_graph.hpp"

using rql::Json;

namespace {

int passed = 0, failed = 0;
std::string repo_root = "..";

void check(bool ok, const std::string& what) {
    if (ok) ++passed;
    else {
        ++failed;
        std::cout << "FAIL " << what << "\n";
    }
}

template <class F>
bool throws(F f) {
    try {
        f();
    } catch (const std::exception&) {
        return true;
    }
    return false;
}

// ------------------------------------------------------------------ JSON

void json_tests() {
    check(Json::parse("\"\\u00e9\"").as_string() == "\xC3\xA9", "json: \\u00e9 → UTF-8");
    check(Json::parse("\"\\ud83d\\ude00\"").as_string() == "\xF0\x9F\x98\x80", "json: surrogate pair → U+1F600");
    check(throws([] { Json::parse("\"\\ud83d\""); }), "json: lone high surrogate rejected");
    check(throws([] { Json::parse("\"\\ude00\""); }), "json: lone low surrogate rejected");
    check(throws([] { Json::parse("\"\xC3\""); }), "json: truncated UTF-8 rejected");
    check(throws([] { Json::parse("\"\xC0\xAF\""); }), "json: overlong UTF-8 rejected");
    check(throws([] { Json::parse("\"a\nb\""); }), "json: raw control char rejected");
    check(Json::parse("\"\xF0\x9F\x98\x80\"").as_string() == "\xF0\x9F\x98\x80", "json: raw 4-byte UTF-8 kept");
    check(throws([] { Json::parse("01"); }), "json: leading zero rejected");
    check(throws([] { Json::parse("1."); }), "json: '1.' rejected");
    check(throws([] { Json::parse("-"); }), "json: '-' rejected");
    check(throws([] { Json::parse("[1,]"); }), "json: trailing comma rejected");
    check(throws([] { Json::parse("{\"a\":1} x"); }), "json: trailing garbage rejected");
    check(throws([] { Json::parse("NaN"); }), "json: NaN literal rejected");
    check(Json::parse(" [ ] ").is_array() && Json::parse("{}").is_object(), "json: empty containers");
    Json n = Json::parse("1.0");
    check(n.as_number() == 1 && !n.integral_text(), "json: 1.0 is not integral text");
    check(Json::parse("12").integral_text() && !Json::parse("1e2").integral_text(), "json: integral text detection");
    check(Json::parse("-0").as_number() == 0 && std::signbit(Json::parse("-0").as_number()), "json: -0 parsed");
    check(Json::parse("1e400").as_number() == HUGE_VAL, "json: overflow → inf");
    Json dup = Json::parse("{\"a\":1,\"a\":2}");
    check(dup.find("a")->as_number() == 2, "json: duplicate key → last wins");
    check(Json::parse("6.666666666666667").as_number() == 20.0 / 3.0, "json: correct rounding");

    check(Json(0.1).dump() == "0.1", "dump: 0.1");
    check(Json(1e21).dump() == "1e+21" || Json(1e21).dump() == "1000000000000000000000", "dump: 1e21 round-trips");
    check(Json(1025.5).dump() == "1025.5", "dump: 1025.5");
    check(Json(3.0).dump() == "3", "dump: integral double without fraction");
    check(Json(-4.0).dump() == "-4", "dump: -4");
    check(Json(9007199254740993.0).dump() != "", "dump: > 2^53");
    double third = 1.0 / 3.0;
    check(Json::parse(Json(third).dump()).as_number() == third, "dump: shortest round-trip 1/3");
    check(Json(5e-324).dump() == "5e-324", "dump: denormal");
    check(Json(std::nan("")).dump() == "null", "dump: NaN → null");
    check(Json("a\"b\\c\n\x01").dump() == "\"a\\\"b\\\\c\\n\\u0001\"", "dump: escapes");
    check(Json("\xC3\xA9").dump() == "\"\xC3\xA9\"", "dump: non-ASCII raw");
    Json o = Json::object();
    o.set("b", 1);
    o.set("a", Json::array());
    check(o.dump() == "{\"b\":1,\"a\":[]}", "dump: object keeps insertion order");
    check(rql::json_equal(Json::parse("{\"a\":1,\"b\":[1,2.0]}"), Json::parse("{\"b\":[1.0,2],\"a\":1}")), "json_equal: order + numbers");
    check(!rql::json_equal(Json::parse("[1,2]"), Json::parse("[2,1]")), "json_equal: arrays ordered");
    Json deep;
    std::string nested(600, '[');
    check(throws([&] { Json::parse(nested); }), "json: nesting limit");

    check(rql::format_number_text(1e15) == "1E+15", "R format: 1E+15");
    check(rql::format_number_text(123456789012345.0) == "123456789012345", "R format: 1.2e14 fixed");
    check(rql::format_number_text(1e-5) == "1E-05", "R format: 1E-05");
    check(rql::format_number_text(0.0001) == "0.0001", "R format: 0.0001");
    check(rql::format_number_text(2.5) == "2.5", "R format: 2.5");
    check(rql::format_number_text(-0.25) == "-0.25", "R format: -0.25");
    check(rql::format_float(0.1f) == "0.1", "float shortest: 0.1f");
    check(rql::utf16_length("a\xC3\xA9\xF0\x9F\x98\x80") == 4, "utf16 length");
}

// ------------------------------------------------------------------ codec

void codec_tests() {
    auto p = rql::decode_plan(std::string_view(R"({"rql":"0.1","source":{"path":["g"]},"stages":[{"op":"first","n":1.0}]})"));
    check(p.stages[0].n == 1, "codec: n=1.0 accepted as integer");
    check(throws([] { rql::decode_plan(std::string_view("{not json")); }), "codec: invalid JSON rejected");
    try {
        rql::decode_plan(std::string_view("{not json"));
    } catch (const rql::RqlException& e) {
        check(e.code() == "invalid-plan", "codec: invalid JSON → invalid-plan");
    }
    rql::QueryPlan built;
    built.source = rql::plan::path("g.Nodes");
    rql::Stage st;
    st.kind = rql::StageKind::Count;
    st.op = "count";
    built.stages.push_back(st);
    check(rql::serialize_plan(built) == R"({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"count"}]})", "codec: canonical serialize");
    // group-by is no longer terminal
    check(!throws([] {
              rql::decode_plan(std::string_view(R"({"rql":"0.1","source":{"path":["g"]},"stages":[{"op":"group-by","expr":{"path":["Kind"]}},{"op":"first","n":1}]})"));
          }),
          "codec: group-by may be followed by stages");
}

// ------------------------------------------------------------------ engine features

struct Counter {
    int Id = 0;
    std::string Name;
    int Count = 0;
    std::vector<double> Samples;
    int Add(int n) { Count += n; return Count; }
    std::string Describe(const std::string& prefix) const { return prefix + Name; }
    void Fail() { throw std::runtime_error("boom"); }
    void Secret() {}
};

// Polymorphic hierarchy WITHOUT rql_dynamic_type(): uses the provider dynamic_type hook (no RTTI).
struct Shape {
    virtual ~Shape() = default;
    virtual int tag() const noexcept { return 0; }
    int Id = 0;
    double Area() const noexcept { return 0; }
};
struct Circle : Shape {
    int tag() const noexcept override { return 1; }
    double Radius = 2;
};
struct Canvas {
    std::vector<std::unique_ptr<Shape>> Shapes;
};

struct Frame {
    conformance::Vec2 Origin;
};

struct Holder {
    std::unordered_map<std::string, int> Scores;
    std::vector<std::shared_ptr<Counter>> Counters;
    std::shared_ptr<Counter> Favorite;
    std::vector<conformance::Vec2> Points;
    bool Locked = false;
};

rql::QueryResult run(rql::Engine& e, const std::string& plan, bool mutate = false, bool dry = false) {
    rql::QueryOptions o;
    o.allow_mutation = mutate;
    o.dry_run = dry;
    return e.execute_json(plan, {}, o);
}

std::string first_code(const rql::QueryResult& r) { return r.diagnostics.empty() ? "" : r.diagnostics[0].code; }

void engine_tests() {
    rql::TypeRegistry reg;
    conformance::register_types(reg);
    reg.object<Counter>("Counter")
        .member("Id", &Counter::Id)
        .member("Name", &Counter::Name)
        .member("Count", &Counter::Count)
        .member("Samples", &Counter::Samples)
        .property("Double", [](Counter& c) { return c.Count * 2; })
        .method("Add", &Counter::Add, {"n"})
        .method("Describe", &Counter::Describe, {"prefix"})
        .method("Fail", &Counter::Fail)
        .method("Secret", &Counter::Secret).not_callable();
    reg.object<Holder>("Holder")
        .member("Scores", &Holder::Scores)
        .member("Counters", &Holder::Counters)
        .member("Favorite", &Holder::Favorite)
        .member("Points", &Holder::Points)
        .member("Locked", &Holder::Locked);

    Holder h;
    h.Scores = {{"zeta", 3}, {"alpha", 1}, {"mid", 2}};
    for (int i = 1; i <= 3; ++i) {
        auto c = std::make_shared<Counter>();
        c->Id = i;
        c->Name = "c" + std::to_string(i);
        c->Samples = {1.5, 2.5};
        h.Counters.push_back(c);
    }
    h.Points = {{1, 2}, {3, 4}};

    reg.object<Shape>("Shape")
        .member("Id", &Shape::Id)
        .property("Tag", [](Shape& sh) noexcept { return sh.tag(); })  // noexcept provider callback
        .method("Area", &Shape::Area)                                   // const noexcept method
        .dynamic_type([](const Shape& sh) noexcept { return sh.tag() == 1 ? rql::type_key<Circle>() : rql::type_key<Shape>(); });
    reg.object<Circle>("Circle").base<Shape>().member("Radius", &Circle::Radius);
    reg.object<Canvas>("Canvas").member("Shapes", &Canvas::Shapes);
    {
        Canvas canvas;
        canvas.Shapes.push_back(std::make_unique<Shape>());
        canvas.Shapes.push_back(std::make_unique<Circle>());
        canvas.Shapes[0]->Id = 1;
        canvas.Shapes[1]->Id = 2;
        rql::Engine ce(reg);
        ce.add_object_source("c", &canvas);
        ce.mutations().enabled = true;
        auto cr = run(ce, R"({"rql":"0.1","source":{"path":["c","Shapes"]},"stages":[{"op":"select","fields":[{"name":"t","expr":{"path":[{"meta":"type"}]}},{"name":"r","expr":{"path":["Radius"]}},{"name":"g","expr":{"path":["Tag"]}},{"name":"ref","expr":{"path":[{"meta":"ref"}]}}]}]})");
        check(cr.ok && (*cr.rows)[0][0].as_string() == "Shape" && (*cr.rows)[1][0].as_string() == "Circle" &&
                  (*cr.rows)[1][1].as_number() == 2 && (*cr.rows)[1][2].as_number() == 1 && (*cr.rows)[1][3].as_string() == "Circle#2",
              "dynamic_type hook resolves most-derived type without RTTI");
        cr = run(ce, R"({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Shape#2"}]},"stages":[{"op":"call","method":"Area","args":[]}]})", true);
        check(cr.ok && (*cr.rows)[0][2].as_number() == 0, "noexcept const method callable (inherited)");
    }
    {
        // Registration errors never throw out of the Engine API.
        rql::Engine bad(reg);
        bad.add_stage("nonamespace", {});
        check(!bad.last_error().empty() && bad.stages().empty(), "invalid registration reported via last_error()");
        auto br = bad.execute_request(std::string("{not json"));
        check(!br.ok && first_code(br) == "invalid-plan", "execute_request(text) noexcept → invalid-plan");
        rql::QueryPlan empty;
        br = bad.execute(empty);
        check(!br.ok && first_code(br) == "invalid-plan", "execute(empty plan) noexcept → invalid-plan");
        static_assert(noexcept(bad.execute(empty)), "Engine::execute must be noexcept");
        static_assert(noexcept(bad.execute_json(std::string())), "Engine::execute_json must be noexcept");
        static_assert(noexcept(bad.capabilities()), "Engine::capabilities must be noexcept");
    }

    reg.object<Frame>("Frame").member("Origin", &Frame::Origin).readonly();
    Frame frame;
    {
        rql::Engine fe(reg);
        fe.add_object_source("f", &frame);
        fe.mutations().enabled = true;
        auto fr = run(fe, R"({"rql":"0.1","source":{"path":["f"]},"stages":[{"op":"set","assign":[{"target":{"path":["Origin","x"]},"value":{"lit":1}}]}]})", true);
        check(!fr.ok && first_code(fr) == "readonly-member" && frame.Origin.x == 0, "set through read-only struct parent → readonly-member");
    }

    rql::Engine e(reg);
    e.add_object_source("h", &h);
    e.mutations().enabled = true;

    // unordered_map iterates sorted by key (deterministic)
    auto r = run(e, R"({"rql":"0.1","source":{"path":["h","Scores"]},"stages":[{"op":"select","fields":[{"name":"k","expr":{"path":["key"]}}]}]})");
    check(r.ok && r.rows && (*r.rows)[0][0].as_string() == "alpha" && (*r.rows)[2][0].as_string() == "zeta", "unordered_map sorted iteration");

    // computed property
    r = run(e, R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"sum","expr":{"path":["Double"]}}]})");
    check(r.ok && r.value.as_number() == 0, "computed property");

    // call stage
    r = run(e, R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"call","method":"add","args":[{"lit":5}]}]})", true);
    check(r.ok && h.Counters[0]->Count == 5 && h.Counters[2]->Count == 5, "call: invokes allowlisted method");
    check(r.ok && (*r.rows)[0][1].as_string() == "Add(int n)" && (*r.rows)[0][2].as_number() == 5, "call: receipt signature/result");
    r = run(e, R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"call","method":"Add","args":[{"lit":5}]}]})", true, true);
    check(r.ok && r.dry_run && h.Counters[0]->Count == 5 && (*r.rows)[0][2].as_string() == "(dry-run)", "call: dry-run");
    r = run(e, R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"call","method":"Add","args":[{"lit":1.5}]}]})", true);
    check(!r.ok && first_code(r) == "coercion-failed" && h.Counters[0]->Count == 5, "call: argument coercion");
    r = run(e, R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"call","method":"Secret","args":[]}]})", true);
    check(!r.ok && first_code(r) == "permission-denied", "call: non-callable → permission-denied");
    r = run(e, R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"call","method":"Nope","args":[]}]})", true);
    check(!r.ok && first_code(r) == "unknown-function", "call: unknown method");
    r = run(e, R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"call","method":"Fail","args":[]}]})", true);
    check(!r.ok && first_code(r) == "call-threw", "call: throwing method → call-threw");
    r = run(e, R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"first","n":1},{"op":"call","method":"Describe","args":[{"lit":"#"}]}]})", true);
    check(r.ok && (*r.rows)[0][2].as_string() == "#c1", "call: const method + string arg");

    // struct write-back through a list element
    r = run(e, R"({"rql":"0.1","source":{"path":["h"]},"stages":[{"op":"set","assign":[{"target":{"path":["Points",{"index":1},"x"]},"value":{"lit":9}}]}]})", true);
    check(r.ok && h.Points[1].x == 9 && h.Points[1].y == 4, "set: struct write-back via list element");

    // shared_ptr member assignment from a shared-owned object
    r = run(e, R"({"rql":"0.1","source":{"path":["h"]},"stages":[{"op":"set","assign":[{"target":{"path":["Favorite"]},"value":{"path":["Counters",{"index":2}]}}]}]})", true);
    check(r.ok && h.Favorite == h.Counters[2], "set: shared_ptr member from shared object");

    // all-or-nothing: second assignment fails coercion → nothing written
    r = run(e, R"({"rql":"0.1","source":{"path":["h"]},"stages":[{"op":"set","assign":[{"target":{"path":["Locked"]},"value":{"lit":true}},{"target":{"path":["Scores","alpha"]},"value":{"lit":"x"}}]}]})", true);
    check(!r.ok && first_code(r) == "coercion-failed" && !h.Locked, "set: all-or-nothing");

    // host write policy
    e.mutations().can_write = [](const rql::TypeDescriptor&, const rql::MemberDescriptor& m) { return m.name != "Locked"; };
    r = run(e, R"({"rql":"0.1","source":{"path":["h"]},"stages":[{"op":"set","assign":[{"target":{"path":["Locked"]},"value":{"lit":true}}]}]})", true);
    check(!r.ok && first_code(r) == "permission-denied", "set: host policy → permission-denied");
    e.mutations().can_write = nullptr;

    // identity members are read-only even without readonly()
    r = run(e, R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"set","assign":[{"target":{"path":["Id"]},"value":{"lit":7}}]}]})", true);
    check(!r.ok && first_code(r) == "readonly-member", "set: identity member is read-only");

    // extension function + stage
    e.add_function("demo.twice", [](const std::vector<rql::Value>& a) {
        double d = a.empty() || !a[0].is_number() ? 0 : a[0].as_number();
        return rql::Value::number(d * 2);
    });
    e.add_stage("demo.reverse", [](const std::vector<rql::Value>& in, const std::vector<rql::ArgEvaluator>&,
                                   const std::map<std::string, rql::ArgEvaluator>&) {
        return std::vector<rql::Value>(in.rbegin(), in.rend());
    });
    r = run(e, R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"demo.reverse"},{"op":"select","fields":[{"name":"n","expr":{"call":"twice","on":{"path":["demo"]},"args":[{"path":["Id"]}]}}]}]})");
    check(r.ok && (*r.rows)[0][0].as_number() == 6 && (*r.rows)[2][0].as_number() == 2, "extension function (ns.fn rewrite) + stage");
    r = run(e, R"({"rql":"0.1","source":{"path":["h"]},"stages":[{"op":"demo.nope"}]})");
    check(!r.ok && first_code(r) == "unknown-stage", "unknown extension stage");

    // session handles + stable refs
    r = run(e, R"({"rql":"0.1","source":{"path":["h"]},"stages":[{"op":"select","fields":[{"name":"r","expr":{"path":[{"meta":"ref"}]}}]}]})");
    std::string handle = r.ok ? (*r.rows)[0][0].as_string() : "";
    check(handle.rfind("Holder@", 0) == 0, "session handle for identity-less object");
    r = run(e, R"({"rql":"0.1","source":{"call":"ref","args":[{"lit":")" + handle + R"("}]},"stages":[{"op":"count"}]})");
    check(r.ok && r.value.as_number() == 1, "resolve session handle");
    r = run(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Counter#2 (c2)"}]},"stages":[{"op":"select","fields":[{"name":"n","expr":{"path":["Name"]}}]}]})J");
    check(r.ok && (*r.rows)[0][0].as_string() == "c2", "resolve stable ref with copied display form");

    // liveness hook → stale-ref
    e.is_alive = [](const rql::Value& v) { return !(v.type()->name == "Counter"); };
    r = run(e, R"({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Counter#2"}]},"stages":[{"op":"count"}]})");
    check(!r.ok && first_code(r) == "stale-ref", "liveness hook → stale-ref");
    e.is_alive = nullptr;

    // custom resolver hook
    e.resolve_stable_ref = [&](const std::string& type, const std::string& id) {
        if (type == "Counter" && id == "42") return reg.wrap(h.Counters[0].get());
        return rql::Value::missing();
    };
    r = run(e, R"({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Counter#42"}]},"stages":[{"op":"select","fields":[{"name":"n","expr":{"path":["Name"]}}]}]})");
    check(r.ok && (*r.rows)[0][0].as_string() == "c1", "resolve_stable_ref hook");
    e.resolve_stable_ref = nullptr;

    // describe + capabilities
    r = run(e, R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"describe"}]})");
    check(r.ok && r.kind == rql::ResultKind::Schema && r.types && r.types->size() == 1 && r.types->at(0).find("methods"), "describe");
    Json caps = e.capabilities();
    check(caps.find("runtime")->as_string() == "cpp" && caps.find("types")->size() >= 5, "capabilities manifest");

    // text requests need the parser sidecar
    Json req = Json::object();
    req.set("text", "h | count");
    r = e.execute_request(req);
    check(!r.ok && first_code(r) == "unsupported", "text request → unsupported");

    // hostile nesting is rejected, not a stack overflow
    {
        std::string deep;
        for (int i = 0; i < 5000; ++i) deep += "{\"op\":\"not\",\"args\":[";
        deep += "{\"lit\":true}";
        for (int i = 0; i < 5000; ++i) deep += "]}";
        r = e.execute_json(R"({"rql":"0.1","source":{"path":["h"]},"stages":[{"op":"where","pred":)" + deep + "}]}");
        check(!r.ok && first_code(r) == "invalid-plan", "deep plan nesting → invalid-plan");
    }

    // throwing host extension stage → internal-error (never propagates)
    e.add_stage("demo.boom", [](const std::vector<rql::Value>&, const std::vector<rql::ArgEvaluator>&,
                                const std::map<std::string, rql::ArgEvaluator>&) -> std::vector<rql::Value> {
        throw std::runtime_error("host bug");
    });
    r = run(e, R"({"rql":"0.1","source":{"path":["h"]},"stages":[{"op":"demo.boom"}]})");
    check(!r.ok && first_code(r) == "internal-error", "throwing extension → internal-error");

    // request maxMutationTargets is capped by the host ceiling
    e.mutations().hard_max_targets = 2;
    {
        rql::Budgets mb;
        mb.max_mutation_targets = 100;
        rql::QueryOptions mo;
        mo.allow_mutation = true;
        r = e.execute_json(R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"set","assign":[{"target":{"path":["Count"]},"value":{"lit":1}}]}]})", mb, mo);
        check(!r.ok && first_code(r) == "mutation-limit", "hard_max_targets caps request budget");
    }
    e.mutations().hard_max_targets = 1000;

    // value-type items cannot be mutation targets
    r = run(e, R"({"rql":"0.1","source":{"path":["h","Points"]},"stages":[{"op":"set","assign":[{"target":{"path":["x"]},"value":{"lit":1}}]}]})", true);
    check(!r.ok && first_code(r) == "invalid-plan", "struct items rejected as set targets");

    // timeout budget
    rql::Budgets b;
    b.max_visited = 1;
    r = e.execute_json(R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"count"}]})", b);
    check(!r.ok && first_code(r) == "budget-exceeded", "maxVisited budget");
}


// ------------------------------------------------------------------ type adapters (spec §13)

struct Base {
    int Id = 0;
    std::string Name;
};
struct Derived : Base {
    int Extra = 0;
};
struct Settings {  // identity-less, nameless: encoded inline
    int Volume = 3;
    std::string Secret = "s";
};
struct Holder2 {
    Settings Prefs;
};
struct Item {  // identity-less, nameless: session handles
    int V = 7;
};
struct Box {
    std::vector<std::unique_ptr<Item>> Items;
};
struct Counter2 {
    int Id = 0;
    int Count = 0;
};
struct Bag {
    std::vector<std::unique_ptr<Counter2>> Counters;
};
struct Thing {
    int Id = 1;
    std::string Name = "thing";
};
struct Shelf {
    std::vector<std::unique_ptr<Thing>> Things;
};
// Unreal-style: a dynamic material instance's scalar parameters + an actor visibility method.
struct MaterialInstanceDynamic {
    std::vector<std::pair<std::string, double>> scalars{{"Roughness", 0.5}, {"Metallic", 0.0}, {"Opacity", 1.0}};
    double* find(const std::string& n) {
        for (auto& kv : scalars)
            if (kv.first == n) return &kv.second;
        return nullptr;
    }
};
struct Actor {
    int Id = 0;
    std::string Name;
    bool bHiddenInGame = false;
    MaterialInstanceDynamic Material;
    int log_calls = 0;
};
struct Level {
    std::vector<std::unique_ptr<Actor>> Actors;
};
// Unreal-style parity fixtures (hooks, write-back, exclusion, pending-kill).
struct FVector3 {
    double X = 0, Y = 0, Z = 0;
};
struct UMaterialInstance {
    std::string Name = "MI_Lamp";
    double Roughness = 0.5;
};
struct NativeHandle {
    int fd = 3;
};
struct UEmitter {
    double StartSpeed = 1;
};
struct FMainModule {  // a handle struct: copies write through to the emitter
    UEmitter* emitter = nullptr;
};
struct UStaticMeshComponent {
    std::string Name = "Mesh";
    FVector3 RelativeLocation;
    UMaterialInstance* Material = nullptr;
    NativeHandle Handle;
    std::vector<NativeHandle> Handles{NativeHandle{}};
    int DeprecatedFlags = 7;
    bool bPendingKill = false;
    UEmitter Emitter;
    std::vector<FVector3> Path{FVector3{}};
};
struct UWorld {
    std::vector<std::unique_ptr<UStaticMeshComponent>> Components;
    std::vector<std::unique_ptr<UMaterialInstance>> Materials;
};

rql::QueryResult arun(rql::Engine& e, const std::string& plan, bool mutate = false) {
    rql::QueryOptions o;
    o.allow_mutation = mutate;
    return e.execute_json(plan, {}, o);
}

void adapter_tests() {
    // ranking + explain
    {
        rql::TypeRegistry reg;
        reg.object<Base>("Base").member("Id", &Base::Id).member("Name", &Base::Name).also_is("IThing");
        reg.object<Derived>("Derived").base<Base>().member("Extra", &Derived::Extra);
        rql::Engine e(reg);
        auto mk = [&](std::string name, const rql::TypeDescriptor* target, int prio, std::string iface = {}) {
            rql::TypeAdapter a;
            a.name = std::move(name);
            a.target = target;
            a.priority = prio;
            a.interface_name = std::move(iface);
            return a;
        };
        e.add_adapter(mk("IfaceAdapter", nullptr, 99, "IThing"));
        e.add_adapter(mk("BaseLow", reg.descriptor<Base>(), 0));
        e.add_adapter(mk("BaseHigh", reg.descriptor<Base>(), 5));
        e.add_adapter(mk("BaseLow2", reg.descriptor<Base>(), 0));
        e.add_adapter(mk("DerivedAdapter", reg.descriptor<Derived>(), -1));
        auto ex = e.explain(*reg.descriptor<Derived>());
        bool order = ex.size() == 5 && ex[0].rfind("DerivedAdapter (target Derived, rank 0", 0) == 0 &&
                     ex[1].rfind("BaseHigh (target Base, rank 1, priority 5)", 0) == 0 && ex[2].rfind("BaseLow (", 0) == 0 &&
                     ex[3].rfind("BaseLow2 (", 0) == 0 && ex[4].rfind("IfaceAdapter (target interface IThing, rank 10000", 0) == 0;
        check(order, "adapters: ranking distance < priority < registration, interfaces last");
        check(e.explain(*reg.descriptor<Base>()).size() == 4, "adapters: subclass adapter does not apply to base");
        e.add_adapter(rql::TypeAdapter{});
        check(!e.last_error().empty(), "adapters: adapter without target rejected");
    }

    rql::TypeRegistry reg;
    conformance::register_types(reg);
    std::string graph_path = repo_root + "/fixtures/cross-runtime/graph.json";

    // stable id + liveness (stale-ref)
    {
        auto g = conformance::load_graph(graph_path);
        rql::Engine e(reg);
        e.add_object_source("g", g.get());
        rql::TypeAdapter idad;
        idad.name = "NodeIds";
        idad.target = reg.descriptor<conformance::Node>();
        rql::TypeRegistry* r = &reg;
        idad.stable_id = [r](const rql::Value& v) -> std::optional<std::string> {
            auto* n = rql::value_as<conformance::Node>(*r, v);
            return n ? std::optional<std::string>("n" + std::to_string(n->Id)) : std::nullopt;
        };
        idad.is_alive = [r](const rql::Value& v) -> std::optional<bool> {
            auto* n = rql::value_as<conformance::Node>(*r, v);
            if (!n) return std::nullopt;
            return n->Id != 5;
        };
        e.add_adapter(std::move(idad));
        auto res = arun(e, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"first","n":1},{"op":"select","fields":[{"name":"r","expr":{"path":[{"meta":"ref"}]}}]}]})J");
        check(res.ok && (*res.rows)[0][0].as_string() == "Node#n1", "adapters: stable_id drives $ref");
        res = arun(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Special#n3"}]},"stages":[{"op":"select","fields":[{"name":"n","expr":{"path":["Name"]}}]}]})J");
        check(res.ok && (*res.rows)[0][0].as_string() == "a-2", "adapters: ref search uses stable_id (inherited by subclass)");
        res = arun(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#n5"}]},"stages":[{"op":"count"}]})J");
        check(!res.ok && first_code(res) == "stale-ref", "adapters: is_alive=false → stale-ref");
        res = arun(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#1"}]},"stages":[{"op":"count"}]})J");
        check(!res.ok && first_code(res) == "stale-ref", "adapters: id member no longer matches when stable_id is adapted");
    }

    // the fixture adapter set, through roots reachable without Children (explicit-only)
    auto g = conformance::load_graph(graph_path);
    rql::Engine e(reg);
    e.add_object_source("g", g.get());
    e.mutations().enabled = true;
    conformance::register_test_adapters(e, reg);
    auto res = arun(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#1"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Pos"]},"value":{"lit":"(5, 6)"}}]}]})J", true);
    check(res.ok && g->Nodes[0]->Pos.x == 5 && g->Nodes[0]->Pos.y == 6 && (*res.rows)[0][2].as_string() == "(0, 0)" &&
              (*res.rows)[0][3].as_string() == "(5, 6)",
          "adapters: coerce string literal to struct (receipt encoded by adapter)");
    res = arun(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#1"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Pos"]},"value":{"list":[{"lit":7},{"lit":8.5}]}}]}]})J", true);
    check(res.ok && g->Nodes[0]->Pos.x == 7 && g->Nodes[0]->Pos.y == 8.5, "adapters: coerce list to struct");
    res = arun(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#1"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Pos"]},"value":{"lit":"nope"}}]}]})J", true);
    check(!res.ok && first_code(res) == "coercion-failed" && res.diagnostics[0].message.find("expected (x, y) or [x, y]") != std::string::npos,
          "adapters: coerce failure carries the reason");
    res = arun(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#5"}]},"stages":[{"op":"set","assign":[{"target":{"path":["title"]},"value":{"lit":"Root B2"}}]}]})J", true);
    check(res.ok && g->Nodes[1]->Name == "root b2" && (*res.rows)[0][2].as_string() == "ROOT-B" && (*res.rows)[0][3].as_string() == "ROOT B2",
          "adapters: virtual setter (receipt reads back the stored value)");
    res = arun(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#5"}]},"stages":[{"op":"set","assign":[{"target":{"path":["title"]},"value":{"lit":""}}]}]})J", true);
    check(!res.ok && first_code(res) == "invalid-value" && g->Nodes[1]->Name == "root b2", "adapters: validation rejects before any write");
    res = arun(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#5"}]},"stages":[{"op":"set","assign":[{"target":{"path":["childCount"]},"value":{"lit":5}}]}]})J", true);
    check(!res.ok && first_code(res) == "readonly-member", "adapters: read-only virtual member");
    res = arun(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#5"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Note"]},"value":{"lit":"x"}}]}]})J", true);
    check(!res.ok && first_code(res) == "unknown-member", "adapters: hidden member unwritable");
    res = arun(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#3"}]},"stages":[{"op":"count"}]})J");
    check(!res.ok && first_code(res) == "stale-ref", "adapters: explicit-only Children not followed by ref search");
    // rollback through the virtual setter: Node#1 written, then Node#3 (locked) throws
    res = arun(e, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"first","n":1},{"op":"get","expr":{"list":[{"path":[]},{"path":["Children",{"index":1}]}]}},{"op":"set","assign":[{"target":{"path":["title"]},"value":{"lit":"x"}}]}]})J", true);
    check(!res.ok && first_code(res) == "coercion-failed" && g->Nodes[0]->Name == "root-a" && g->Nodes[0]->Children[1]->Name == "a-2",
          "adapters: setter throwing at write time rolls back earlier virtual writes");
    res = arun(e, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"where","pred":{"op":"==","args":[{"lit":"(10, 10)"},{"path":["Pos"]}]}},{"op":"count"}]})J");
    check(res.ok && res.value.as_number() == 1, "adapters: right-operand adapter equality (mirrored)");
    res = arun(e, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"where","pred":{"op":"<","args":[{"lit":"(100, 0)"},{"path":["Pos"]}]}},{"op":"count"}]})J");
    check(res.ok && res.value.as_number() == 0, "adapters: right-operand ordering mirrored");

    // explicit-only skipped in inline encoding; describe lists explicitOnly
    {
        rql::TypeRegistry r2;
        r2.object<Settings>("Settings").member("Volume", &Settings::Volume).member("Secret", &Settings::Secret);
        r2.object<Holder2>("Holder2").member("Prefs", &Holder2::Prefs);
        Holder2 h2;
        rql::Engine e2(r2);
        e2.add_object_source("h", &h2);
        rql::TypeAdapter a;
        a.target = r2.descriptor<Settings>();
        a.configure_members = [](rql::MemberTable& t) { t.explicit_only({"Secret"}); };
        e2.add_adapter(std::move(a));
        auto r = arun(e2, R"J({"rql":"0.1","source":{"path":["h"]},"stages":[{"op":"select","fields":[{"name":"p","expr":{"path":["Prefs"]}},{"name":"s","expr":{"path":["Prefs","Secret"]}}]}]})J");
        check(r.ok && (*r.rows)[0][0].find("Volume") && !(*r.rows)[0][0].find("Secret") && (*r.rows)[0][1].as_string() == "s",
              "adapters: explicit-only skipped by inline encoding, readable by name");
        r = arun(e2, R"J({"rql":"0.1","source":{"path":["h","Prefs"]},"stages":[{"op":"describe"}]})J");
        bool flagged = false;
        if (r.ok && r.types)
            for (auto& m : r.types->at(0).find("members")->items())
                if (m.find("name")->as_string() == "Secret" && m.find("explicitOnly")) flagged = true;
        check(flagged, "adapters: describe marks explicitOnly");
    }

    // distinct on structs compares implicit members only (explicit-only / hidden are never read implicitly)
    {
        auto g3 = conformance::load_graph(graph_path);
        rql::Engine de(reg);
        de.add_object_source("g", g3.get());
        auto r = arun(de, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"recurse","via":[{"path":["Children"]}]},{"op":"get","expr":{"path":["Pos"]}},{"op":"distinct"},{"op":"count"}]})J");
        check(r.ok && r.value.as_number() == 8, "adapters: baseline distinct Pos (no adapters)");
        rql::TypeAdapter xo;
        xo.target = reg.descriptor<conformance::Vec2>();
        bool x_read = false;
        xo.configure_members = [&x_read](rql::MemberTable& t) {
            t.add("x", rql::builtin::double_type(), [&x_read](void* self) {
                x_read = true;
                return rql::Value::number(static_cast<conformance::Vec2*>(self)->x);
            });
            t.explicit_only({"x"});
        };
        de.add_adapter(std::move(xo));
        r = arun(de, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"recurse","via":[{"path":["Children"]}]},{"op":"get","expr":{"path":["Pos"]}},{"op":"distinct"},{"op":"count"}]})J");
        check(r.ok && r.value.as_number() == 7 && !x_read, "adapters: struct distinct skips explicit-only members (getter never called)");
    }

    // adapter liveness also applies to session handles ("Type@n")
    {
        auto g4 = conformance::load_graph(graph_path);
        rql::Engine le(reg);
        le.add_object_source("g", g4.get());
        auto r = arun(le, R"J({"rql":"0.1","source":{"path":["g"]},"stages":[{"op":"select","fields":[{"name":"r","expr":{"path":[{"meta":"ref"}]}}]}]})J");
        std::string handle = r.ok ? (*r.rows)[0][0].as_string() : "";
        rql::TypeAdapter gone;
        gone.target = reg.descriptor<conformance::Graph>();
        gone.is_alive = [](const rql::Value&) -> std::optional<bool> { return false; };
        le.add_adapter(std::move(gone));
        r = arun(le, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":")J" + handle + R"J("}]},"stages":[{"op":"count"}]})J");
        check(handle.rfind("Graph@", 0) == 0 && !r.ok && first_code(r) == "stale-ref", "adapters: adapter is_alive applies to session handles");
    }

    // review fixes ------------------------------------------------------------------------------

    // 1. handle fast path: a liveness adapter for ANOTHER type (or one that defers) must not make a
    //    freed object's stale handle resolve (use-after-free); deciding never dereferences it.
    {
        rql::TypeRegistry r3;
        r3.object<Item>("Item").member("V", &Item::V);
        r3.object<Box>("Box").member("Items", &Box::Items);
        Box box;
        box.Items.push_back(std::make_unique<Item>());
        rql::Engine le(r3);
        le.add_object_source("b", &box);
        rql::TypeAdapter other;  // liveness adapter for Box only
        other.target = r3.descriptor<Box>();
        other.is_alive = [](const rql::Value&) -> std::optional<bool> { return true; };
        le.add_adapter(std::move(other));
        rql::TypeAdapter defers;  // liveness adapter for Item that never answers
        defers.target = r3.descriptor<Item>();
        defers.is_alive = [](const rql::Value&) -> std::optional<bool> { return std::nullopt; };
        le.add_adapter(std::move(defers));
        auto r = arun(le, R"J({"rql":"0.1","source":{"path":["b","Items"]},"stages":[{"op":"select","fields":[{"name":"r","expr":{"path":[{"meta":"ref"}]}}]}]})J");
        std::string handle = r.ok ? (*r.rows)[0][0].as_string() : "";
        std::string ref_plan = R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":")J" + handle +
                               R"J("}]},"stages":[{"op":"select","fields":[{"name":"v","expr":{"path":["V"]}}]}]})J";
        r = arun(le, ref_plan);
        check(handle.rfind("Item@", 0) == 0 && r.ok && (*r.rows)[0][0].as_number() == 7, "review: live handle still resolves (reachable)");
        box.Items.clear();  // frees the Item
        r = arun(le, ref_plan);
        check(!r.ok && first_code(r) == "stale-ref", "review: freed handle with unrelated/deferring liveness adapters → stale-ref (no UAF)");
    }

    // 2+3. rollback runs for non-std exceptions and also restores the failed write itself
    {
        rql::TypeRegistry r4;
        r4.object<Counter2>("Counter2").member("Id", &Counter2::Id).member("Count", &Counter2::Count);
        r4.object<Bag>("Bag").member("Counters", &Bag::Counters);
        Bag bag;
        for (int i = 1; i <= 2; ++i) {
            bag.Counters.push_back(std::make_unique<Counter2>());
            bag.Counters.back()->Id = i;
        }
        rql::Engine me(r4);
        me.add_object_source("b", &bag);
        me.mutations().enabled = true;
        rql::TypeAdapter a;
        a.target = r4.descriptor<Counter2>();
        a.configure_members = [](rql::MemberTable& t) {
            t.property<Counter2>(
                "level", [](Counter2& c) { return c.Count; },
                [](Counter2& c, int v) {
                    c.Count = v;                       // changes state ...
                    if (c.Id == 2 && v == 5) throw 1;  // ... then throws a non-std exception
                },
                nullptr);
        };
        me.add_adapter(std::move(a));
        auto r = arun(me, R"J({"rql":"0.1","source":{"path":["b","Counters"]},"stages":[{"op":"set","assign":[{"target":{"path":["level"]},"value":{"lit":5}}]}]})J", true);
        check(!r.ok && first_code(r) == "coercion-failed" && bag.Counters[0]->Count == 0 && bag.Counters[1]->Count == 0,
              "review: `throw 1` in a setter rolls back earlier writes and restores the failed write itself");
    }

    // 4. explicit-only id / display are never read implicitly (handles, no name in summaries)
    {
        rql::TypeRegistry r5;
        r5.object<Thing>("Thing").member("Id", &Thing::Id).member("Name", &Thing::Name);
        r5.object<Shelf>("Shelf").member("Things", &Shelf::Things);
        Shelf shelf;
        shelf.Things.push_back(std::make_unique<Thing>());
        rql::Engine te(r5);
        te.add_object_source("s", &shelf);
        bool id_read = false, name_read = false;
        rql::TypeAdapter a;
        a.target = r5.descriptor<Thing>();
        a.configure_members = [&](rql::MemberTable& t) {
            t.computed<Thing>("Id", [&id_read](Thing& th) { id_read = true; return th.Id; });
            t.computed<Thing>("Name", [&name_read](Thing& th) { name_read = true; return th.Name; });
            t.explicit_only({"Id", "Name"});
        };
        te.add_adapter(std::move(a));
        auto r = arun(te, R"J({"rql":"0.1","source":{"path":["s"]},"stages":[{"op":"select","fields":[{"name":"t","expr":{"path":["Things"]}},{"name":"r","expr":{"path":["Things",{"index":0},{"meta":"ref"}]}}]}]})J");
        // No implicit id/display left: $ref is a session handle and the object encodes inline (value-like,
        // spec §6) from its implicit members only — here none, so {}.
        bool ok = r.ok && (*r.rows)[0][1].as_string().rfind("Thing@", 0) == 0 && (*r.rows)[0][0].at(0).is_object() &&
                  (*r.rows)[0][0].at(0).size() == 0;
        check(ok && !id_read && !name_read, "review: explicit-only id/display not used (session handle, inline {}, getters not called)");
        r = arun(te, R"J({"rql":"0.1","source":{"path":["s","Things"]},"stages":[{"op":"select","fields":[{"name":"i","expr":{"path":["Id"]}}]}]})J");
        check(r.ok && (*r.rows)[0][0].as_number() == 1 && id_read, "review: explicit-only id still readable by name");
    }

    // 5. scalar values of the target type bypass coerce adapters on builtin scalars
    {
        auto g5 = conformance::load_graph(graph_path);
        rql::Engine se(reg);
        se.add_object_source("g", g5.get());
        se.mutations().enabled = true;
        rql::TypeAdapter strict;
        strict.target = rql::builtin::string_type();
        strict.coerce = [](const rql::Value&, const rql::TypeDescriptor&, rql::Value&, std::string& why) {
            why = "strings only via the adapter";
            return rql::AdapterOutcome::Failed;
        };
        se.add_adapter(std::move(strict));
        auto r = arun(se, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#1"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Name"]},"value":{"lit":"renamed"}}]}]})J", true);
        check(r.ok && g5->Nodes[0]->Name == "renamed", "review: string value bypasses the string coerce adapter");
        r = arun(se, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#1"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Name"]},"value":{"lit":5}}]}]})J", true);
        check(!r.ok && first_code(r) == "coercion-failed" && r.diagnostics[0].message.find("strings only via the adapter") != std::string::npos,
              "review: other kinds still go through the scalar coerce adapter");
    }

    // 6. hide / only with an unknown member → internal-error
    for (int only = 0; only < 2; ++only) {
        auto g6 = conformance::load_graph(graph_path);
        rql::Engine he(reg);
        he.add_object_source("g", g6.get());
        rql::TypeAdapter a;
        a.target = reg.descriptor<conformance::Node>();
        a.configure_members = [only](rql::MemberTable& t) {
            if (only) t.only({"Name", "Pasword"});
            else t.hide({"Pasword"});
        };
        he.add_adapter(std::move(a));
        auto r = arun(he, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"select","fields":[{"name":"n","expr":{"path":["Name"]}}]}]})J");
        check(!r.ok && first_code(r) == "internal-error", only ? "review: only() with an unknown member → internal-error"
                                                               : "review: hide() with an unknown member → internal-error");
    }

    // ordering adapters apply to min / max (spec §13): custom enum order Gamma < Alpha < Beta
    {
        auto g7 = conformance::load_graph(graph_path);
        rql::Engine oe(reg);
        oe.add_object_source("g", g7.get());
        const rql::TypeDescriptor* kind = reg.descriptor<conformance::NodeKind>();
        rql::TypeAdapter order;
        order.target = kind;
        order.compare = [kind](const rql::Value& v, const rql::Value& other) -> std::optional<int> {
            if (!other.is_enum() || other.type() != kind) return std::nullopt;
            auto rank = [](int64_t e) { return e == 2 ? 0 : (e == 0 ? 1 : 2); };  // Gamma, Alpha, Beta
            int a = rank(v.enum_value()), b = rank(other.enum_value());
            return a < b ? -1 : (a > b ? 1 : 0);
        };
        oe.add_adapter(std::move(order));
        auto r = arun(oe, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"recurse","via":[{"path":["Children"]}]},{"op":"max","expr":{"path":["Kind"]}}]})J");
        check(r.ok && r.value.as_string() == "Beta", "review: max uses the enum ordering adapter (Beta, not numeric Gamma)");
        r = arun(oe, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"recurse","via":[{"path":["Children"]}]},{"op":"min","expr":{"path":["Kind"]}}]})J");
        check(r.ok && r.value.as_string() == "Gamma", "review: min uses the enum ordering adapter (Gamma, not numeric Alpha)");
    }

    // min / max accept values ordered only by an adapter (objects): Nodes ordered by Value
    {
        auto g8 = conformance::load_graph(graph_path);
        rql::Engine oe(reg);
        oe.add_object_source("g", g8.get());
        rql::TypeRegistry* rp = &reg;
        rql::TypeAdapter byValue;
        byValue.target = reg.descriptor<conformance::Node>();
        byValue.compare = [rp](const rql::Value& v, const rql::Value& other) -> std::optional<int> {
            auto* a = rql::value_as<conformance::Node>(*rp, v);
            auto* b = rql::value_as<conformance::Node>(*rp, other);
            if (!a || !b) return std::nullopt;
            return a->Value < b->Value ? -1 : (a->Value > b->Value ? 1 : 0);
        };
        oe.add_adapter(std::move(byValue));
        auto r = arun(oe, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"recurse","via":[{"path":["Children"]}]},{"op":"max","expr":{"path":[]}}]})J");
        check(r.ok && r.value.is_object() && r.value.find("name") && r.value.find("name")->as_string() == "B-2",
              "review: max $ over objects uses the ordering adapter (B-2, Value 1000)");
        r = arun(oe, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"recurse","via":[{"path":["Children"]}]},{"op":"min","expr":{"path":[]}}]})J");
        check(r.ok && r.value.is_object() && r.value.find("name") && r.value.find("name")->as_string() == "a-2",
              "review: min $ over objects uses the ordering adapter (a-2, Value -4)");
    }

    // adapter methods + virtual maps (spec §13), Unreal-style host
    {
        rql::TypeRegistry ur;
        ur.object<Actor>("Actor").member("Id", &Actor::Id).member("Name", &Actor::Name).member("bHiddenInGame", &Actor::bHiddenInGame);
        ur.object<Level>("Level").member("Actors", &Level::Actors);
        Level level;
        for (int i = 1; i <= 2; ++i) {
            level.Actors.push_back(std::make_unique<Actor>());
            level.Actors.back()->Id = i;
            level.Actors.back()->Name = "BP_Lamp" + std::to_string(i);
        }
        rql::Engine ue(ur);
        ue.add_object_source("level", &level);
        ue.mutations().enabled = true;
        rql::TypeAdapter actor;
        actor.target = ur.descriptor<Actor>();
        actor.configure_members = [](rql::MemberTable& t) {
            t.map<Actor>(
                 "ScalarParams",
                 [](Actor& a) {
                     std::vector<std::string> k;
                     for (auto& kv : a.Material.scalars) k.push_back(kv.first);
                     return k;
                 },
                 [](Actor& a, const std::string& k) { return *a.Material.find(k); },
                 [](Actor& a, const std::string& k, double v) { *a.Material.find(k) = v; },
                 [](Actor& a, const std::string& k, const double& v) {
                     if (!a.Material.find(k)) return "material has no scalar parameter '" + k + "'";
                     return (v < 0 || v > 1) ? std::string("scalar parameters are in [0, 1]") : std::string();
                 },
                 "UMaterialInstanceDynamic scalar parameters")
                .method<Actor>(
                    "SetActorHiddenInGame", {rql::ParamDescriptor{"bNewHidden", rql::builtin::bool_type(), false, false, rql::Value()}},
                    nullptr,
                    [](Actor& a, const std::vector<rql::Value>& args) {
                        a.bHiddenInGame = args[0].as_bool();
                        return rql::Value::null();
                    },
                    "hide/show the actor in game")
                .method<Actor>("Log", {}, rql::builtin::number_type(true, true, 4), [](Actor& a, const std::vector<rql::Value>&) {
                    return rql::Value::number(++a.log_calls);
                })
                .method<Actor>(
                    "Log", {rql::ParamDescriptor{"times", rql::builtin::double_type(), false, false, rql::Value()}}, nullptr,
                    [](Actor& a, const std::vector<rql::Value>& args) {
                        a.log_calls += static_cast<int>(args[0].as_number());
                        return rql::Value::null();
                    });
        };
        ue.add_adapter(std::move(actor));
        auto r = arun(ue, R"J({"rql":"0.1","source":{"path":["level","Actors"]},"stages":[{"op":"first","n":1},{"op":"select","fields":[{"name":"p","expr":{"path":["ScalarParams"]}},{"name":"r","expr":{"path":["ScalarParams",{"key":"Roughness"}]}}]}]})J");
        check(r.ok && (*r.rows)[0][0].find("Opacity") && (*r.rows)[0][1].as_number() == 0.5, "methods/maps: virtual map read (whole + key)");
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["level","Actors"]},"stages":[{"op":"set","assign":[{"target":{"path":["ScalarParams",{"key":"Roughness"}]},"value":{"lit":0.25}}]}]})J", true);
        check(r.ok && level.Actors[0]->Material.scalars[0].second == 0.25 && level.Actors[1]->Material.scalars[0].second == 0.25 &&
                  (*r.rows)[0][2].as_number() == 0.5,
              "methods/maps: validated virtual map write (receipt old value)");
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["level","Actors"]},"stages":[{"op":"set","assign":[{"target":{"path":["ScalarParams",{"key":"Metallic"}]},"value":{"lit":0.9}},{"target":{"path":["ScalarParams",{"key":"Opacity"}]},"value":{"lit":2}}]}]})J", true);
        check(!r.ok && first_code(r) == "invalid-value" && r.diagnostics[0].message.find("[0, 1]") != std::string::npos &&
                  level.Actors[0]->Material.scalars[1].second == 0.0,
              "methods/maps: entry validation rejects before any write (all-or-nothing)");
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["level","Actors"]},"stages":[{"op":"set","assign":[{"target":{"path":["ScalarParams",{"key":"Emissive"}]},"value":{"lit":0.1}}]}]})J", true);
        check(!r.ok && first_code(r) == "invalid-value" && level.Actors[0]->Material.scalars.size() == 3, "methods/maps: no new entries beyond the owner's keys");
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["level","Actors"]},"stages":[{"op":"set","assign":[{"target":{"path":["ScalarParams"]},"value":{"lit":1}}]}]})J", true);
        check(!r.ok && first_code(r) == "readonly-member", "methods/maps: the map member itself is not assignable");
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["level","Actors"]},"stages":[{"op":"where","pred":{"op":"==","args":[{"path":["Id"]},{"lit":2}]}},{"op":"call","method":"SetActorHiddenInGame","args":[{"lit":true}]}]})J", true);
        check(r.ok && level.Actors[1]->bHiddenInGame && !level.Actors[0]->bHiddenInGame && (*r.rows)[0][1].as_string() == "SetActorHiddenInGame(bool bNewHidden)" &&
                  (*r.rows)[0][2].is_null(),
              "methods/maps: adapter method call (void result → null)");
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["level","Actors"]},"stages":[{"op":"first","n":1},{"op":"call","method":"setactorhiddeningame","args":[{"lit":"yes"}]}]})J", true);
        check(!r.ok && first_code(r) == "coercion-failed" && !level.Actors[0]->bHiddenInGame, "methods/maps: argument coercion failure, nothing invoked");
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["level","Actors"]},"stages":[{"op":"first","n":1},{"op":"call","method":"Log","args":[]}]})J", true);
        bool zero = r.ok && level.Actors[0]->log_calls == 1 && (*r.rows)[0][1].as_string() == "Log()";
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["level","Actors"]},"stages":[{"op":"first","n":1},{"op":"call","method":"Log","args":[{"lit":3}]}]})J", true);
        check(zero && r.ok && level.Actors[0]->log_calls == 4 && (*r.rows)[0][1].as_string() == "Log(double times)", "methods/maps: overloads chosen by arity");
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["level","Actors"]},"stages":[{"op":"first","n":1},{"op":"describe"}]})J");
        bool listed = false;
        if (r.ok && r.types)
            for (auto& m : r.types->at(0).find("methods")->items())
                if (m.as_string() == "SetActorHiddenInGame(bool bNewHidden) \xE2\x80\x94 hide/show the actor in game") listed = true;
        check(listed, "methods/maps: describe lists adapter methods with description");
    }

    // hide_methods + base-adapter methods on subclasses; unknown names → internal-error
    {
        auto gm = conformance::load_graph(graph_path);
        rql::Engine me(reg);
        me.add_object_source("g", gm.get());
        me.mutations().enabled = true;
        conformance::register_test_adapters(me, reg);
        auto r = arun(me, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"first","n":1},{"op":"get","expr":{"path":["Children",{"index":1}]}},{"op":"call","method":"greet","args":[{"lit":"Bo"}]}]})J", true);
        check(r.ok && (*r.rows)[0][0].as_string() == "Special#3" && (*r.rows)[0][2].as_string() == "hi Bo, I am a-2",
              "methods/maps: Node adapter method callable on Special (upcast)");
        rql::TypeAdapter hider;
        hider.target = reg.descriptor<conformance::Special>();
        hider.configure_members = [](rql::MemberTable& t) { t.hide_methods({"greet"}); };
        me.add_adapter(std::move(hider));
        r = arun(me, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"first","n":1},{"op":"get","expr":{"path":["Children",{"index":1}]}},{"op":"call","method":"greet","args":[{"lit":"Bo"}]}]})J", true);
        check(!r.ok && first_code(r) == "unknown-function", "methods/maps: hide_methods on the subclass");
        rql::Engine bad(reg);
        bad.add_object_source("g", gm.get());
        rql::TypeAdapter b;
        b.target = reg.descriptor<conformance::Node>();
        b.configure_members = [](rql::MemberTable& t) { t.hide_methods({"nope"}); };
        bad.add_adapter(std::move(b));
        r = arun(bad, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"count"}]})J");
        auto r2 = arun(bad, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"select","fields":[{"name":"n","expr":{"path":["Name"]}}]}]})J");
        check(!r2.ok && first_code(r2) == "internal-error", "methods/maps: hide_methods with an unknown name → internal-error");
    }

    // ---- parity: host hooks, write-back, exclusion, pending-kill, object literals (spec §4.5, §13)
    {
        rql::TypeRegistry ur;
        ur.structure<FVector3>("FVector").member("X", &FVector3::X).member("Y", &FVector3::Y).member("Z", &FVector3::Z);
        ur.object<UMaterialInstance>("UMaterialInstance").member("Name", &UMaterialInstance::Name).member("Roughness", &UMaterialInstance::Roughness);
        ur.structure<NativeHandle>("NativeHandle").member("fd", &NativeHandle::fd);
        ur.object<UEmitter>("UEmitter").member("StartSpeed", &UEmitter::StartSpeed).no_identity();
        ur.structure<FMainModule>("FMainModule");
        ur.object<UStaticMeshComponent>("UStaticMeshComponent")
            .member("Name", &UStaticMeshComponent::Name)
            .member("RelativeLocation", &UStaticMeshComponent::RelativeLocation)
            .member("Material", &UStaticMeshComponent::Material).reference()
            .member("Handle", &UStaticMeshComponent::Handle)
            .member("Handles", &UStaticMeshComponent::Handles)
            .member("DeprecatedFlags", &UStaticMeshComponent::DeprecatedFlags)
            .member("Path", &UStaticMeshComponent::Path)
            .property("Main", [](UStaticMeshComponent& c) { return FMainModule{&c.Emitter}; })  // get-only owner
            .constructible();
        ur.object<UWorld>("UWorld").member("Components", &UWorld::Components).member("Materials", &UWorld::Materials);
        UWorld world;
        world.Materials.push_back(std::make_unique<UMaterialInstance>());
        for (int i = 0; i < 2; ++i) {
            world.Components.push_back(std::make_unique<UStaticMeshComponent>());
            world.Components.back()->Name = "Mesh" + std::to_string(i);
            world.Components.back()->Material = world.Materials[0].get();
        }
        rql::Engine ue(ur);
        ue.add_object_source("w", &world);
        ue.mutations().enabled = true;
        ue.exclude_types([](const rql::TypeDescriptor& t) { return t.name == "NativeHandle"; });
        rql::TypeRegistry* rp = &ur;
        rql::TypeAdapter comp;
        comp.target = ur.descriptor<UStaticMeshComponent>();
        comp.is_alive = [rp](const rql::Value& v) -> std::optional<bool> {  // pending-kill objects read as null
            auto* c = rql::value_as<UStaticMeshComponent>(*rp, v);
            return c ? std::optional<bool>(!c->bPendingKill) : std::nullopt;
        };
        comp.configure_members = [](rql::MemberTable& t) {
            t.hide_where([](const rql::MemberDescriptor& m) { return m.name.rfind("Deprecated", 0) == 0; });
        };
        ue.add_adapter(std::move(comp));
        rql::TypeAdapter mainModule;
        mainModule.target = ur.descriptor<FMainModule>();
        mainModule.configure_members = [](rql::MemberTable& t) {
            t.writes_through().property<FMainModule>(
                "StartSpeed", [](FMainModule& m) { return m.emitter->StartSpeed; }, [](FMainModule& m, double v) { m.emitter->StartSpeed = v; },
                nullptr);
        };
        ue.add_adapter(std::move(mainModule));

        std::vector<rql::MutationRecord> before, after;
        int before_calls = 0, after_calls = 0;
        ue.mutations().before_write = [&](const std::vector<rql::MutationRecord>& recs) { ++before_calls; before = recs; };
        ue.mutations().committed = [&](const std::vector<rql::MutationRecord>& recs) { ++after_calls; after = recs; };

        // owner = the material when writing through the component's reference; the component for struct paths
        auto r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Components"]},"stages":[{"op":"first","n":1},{"op":"set","assign":[{"target":{"path":["Material","Roughness"]},"value":{"lit":0.25}},{"target":{"path":["RelativeLocation","Z"]},"value":{"lit":100}}]}]})J", true);
        bool owners = before.size() == 2 && after.size() == 2 && before[0].owner.ptr() == world.Materials[0].get() &&
                      before[0].item.ptr() == world.Components[0].get() && before[0].member == "Material.Roughness" &&
                      before[0].old_value.as_number() == 0.5 && before[0].new_value.as_number() == 0.25 &&
                      before[1].owner.ptr() == world.Components[0].get();
        check(r.ok && owners && world.Materials[0]->Roughness == 0.25 && world.Components[0]->RelativeLocation.Z == 100,
              "parity: hook records carry owner = last reference object (material via component; component for struct path)");
        // veto: before_write throws → permission-denied, nothing written, committed not called
        ue.mutations().before_write = [&](const std::vector<rql::MutationRecord>&) { throw std::runtime_error("asset is checked out"); };
        after_calls = 0;
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Materials"]},"stages":[{"op":"set","assign":[{"target":{"path":["Roughness"]},"value":{"lit":0.9}}]}]})J", true);
        check(!r.ok && first_code(r) == "permission-denied" && world.Materials[0]->Roughness == 0.25 && after_calls == 0,
              "parity: before_write veto → permission-denied, nothing changed");
        ue.mutations().before_write = [&](const std::vector<rql::MutationRecord>&) { throw 42; };  // non-std exception contained
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Materials"]},"stages":[{"op":"set","assign":[{"target":{"path":["Roughness"]},"value":{"lit":0.9}}]}]})J", true);
        check(!r.ok && first_code(r) == "permission-denied" && world.Materials[0]->Roughness == 0.25, "parity: non-std veto contained");
        // dry-run: no hooks
        ue.mutations().before_write = [&](const std::vector<rql::MutationRecord>&) { ++before_calls; };
        before_calls = after_calls = 0;
        r = run(ue, R"J({"rql":"0.1","source":{"path":["w","Materials"]},"stages":[{"op":"set","assign":[{"target":{"path":["Roughness"]},"value":{"lit":0.9}}]}]})J", true, true);
        check(r.ok && r.dry_run && before_calls == 0 && after_calls == 0 && world.Materials[0]->Roughness == 0.25, "parity: no hooks on dry-run");
        // committed failure → hook-threw warning, writes stay
        ue.mutations().committed = [&](const std::vector<rql::MutationRecord>&) { throw std::runtime_error("SetDirty failed"); };
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Materials"]},"stages":[{"op":"set","assign":[{"target":{"path":["Roughness"]},"value":{"lit":0.75}}]}]})J", true);
        bool warned = false;
        for (auto& d : r.diagnostics)
            if (d.code == "hook-threw" && d.severity == rql::Severity::Warning) warned = true;
        check(r.ok && warned && world.Materials[0]->Roughness == 0.75, "parity: committed failure → hook-threw warning, writes applied");
        // owner rule: the nearest enclosing OBJECT (never a map view, list or struct handle)
        rql::TypeAdapter matParams;
        matParams.target = ur.descriptor<UMaterialInstance>();
        matParams.configure_members = [](rql::MemberTable& t) {
            t.map<UMaterialInstance>(
                "ScalarParams", [](UMaterialInstance&) { return std::vector<std::string>{"Roughness"}; },
                [](UMaterialInstance& m, const std::string&) { return m.Roughness; },
                [](UMaterialInstance& m, const std::string&, double v) { m.Roughness = v; });
        };
        ue.add_adapter(std::move(matParams));
        ue.mutations().before_write = [&](const std::vector<rql::MutationRecord>& recs) { before = recs; };
        ue.mutations().committed = nullptr;
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Components"]},"stages":[{"op":"first","n":1},{"op":"set","assign":[{"target":{"path":["Material","ScalarParams",{"key":"Roughness"}]},"value":{"lit":0.2}},{"target":{"path":["Main","StartSpeed"]},"value":{"lit":9}}]}]})J", true);
        check(r.ok && before.size() == 2 && before[0].owner.ptr() == world.Materials[0].get() && before[1].owner.ptr() == world.Components[0].get() &&
                  world.Materials[0]->Roughness == 0.2 && world.Components[0]->Emitter.StartSpeed == 9,
              "review: owner = material for a virtual-map entry, the component for a handle-struct member");
        before.clear();
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Components"]},"stages":[{"op":"first","n":1},{"op":"set","assign":[{"target":{"path":["Material","ScalarParams",{"key":"Metallic"}]},"value":{"lit":0.2}}]}]})J", true);
        check(!r.ok && first_code(r) == "invalid-value" && r.diagnostics[0].message.find("no entry 'Metallic'") != std::string::npos && before.empty(),
              "review: virtual maps reject writes to absent keys while planning");
        ue.mutations().before_write = nullptr;
        ue.mutations().committed = nullptr;

        // writes-through handle: no write-back through the get-only owner, and handle values are mutation targets
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Components"]},"stages":[{"op":"first","n":1},{"op":"set","assign":[{"target":{"path":["Main","StartSpeed"]},"value":{"lit":5}}]}]})J", true);
        check(r.ok && world.Components[0]->Emitter.StartSpeed == 5, "parity: writes-through handle via a get-only owner");
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Components"]},"stages":[{"op":"get","expr":{"path":["Main"]}},{"op":"set","assign":[{"target":{"path":["StartSpeed"]},"value":{"lit":7}}]}]})J", true);
        check(r.ok && world.Components[0]->Emitter.StartSpeed == 7 && world.Components[1]->Emitter.StartSpeed == 7, "parity: writes-through values are mutation targets");
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Components"]},"stages":[{"op":"get","expr":{"path":["RelativeLocation"]}},{"op":"set","assign":[{"target":{"path":["X"]},"value":{"lit":1}}]}]})J", true);
        check(!r.ok && first_code(r) == "invalid-plan", "parity: ordinary structs are still not mutation targets");

        // hide_where + engine-wide type exclusion (type, element type)
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Components"]},"stages":[{"op":"first","n":1},{"op":"describe"}]})J");
        bool hidden = r.ok && r.types;
        if (hidden)
            for (auto& m : r.types->at(0).find("members")->items()) {
                const std::string& n = m.find("name")->as_string();
                if (n == "DeprecatedFlags" || n == "Handle" || n == "Handles") hidden = false;
            }
        check(hidden, "parity: hide_where and type exclusion (member type and element type) drop members");

        // pending-kill: skipped in streams like null, reads as null in collections, never a mutation target
        world.Components[1]->bPendingKill = true;
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Components"]},"stages":[{"op":"select","fields":[{"name":"n","expr":{"path":["Name"]}}]}]})J");
        check(r.ok && r.rows->size() == 1 && (*r.rows)[0][0].as_string() == "Mesh0", "parity: pending-kill object skipped in streams");
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w"]},"stages":[{"op":"select","fields":[{"name":"dead","expr":{"call":"any","on":{"path":["Components"]},"args":[{"op":"==","args":[{"path":[]},{"lit":null}]}]}},{"name":"second","expr":{"path":["Components",{"index":1}]}},{"name":"n","expr":{"path":["Components",{"index":1},"Name"]}}]}]})J");
        check(r.ok && (*r.rows)[0][0].as_bool() && (*r.rows)[0][1].is_null() && (*r.rows)[0][2].is_null(),
              "parity: pending-kill object reads as null in collections and index access (no getter)");
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Components"]},"stages":[{"op":"set","assign":[{"target":{"path":["Name"]},"value":{"lit":"x"}}]}]})J", true);
        check(r.ok && r.rows->size() == 1 && world.Components[0]->Name == "x" && world.Components[1]->Name == "Mesh1",
              "parity: pending-kill mutation target is skipped like a null item");
        world.Components[0]->Name = "Mesh0";
        world.Components[1]->bPendingKill = false;

        // object literals: struct construction with defaults, list of structs, unknown member
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Components"]},"stages":[{"op":"first","n":1},{"op":"set","assign":[{"target":{"path":["RelativeLocation"]},"value":{"object":[{"key":"Y","value":{"lit":2}}]}},{"target":{"path":["Path"]},"value":{"list":[{"object":[{"key":"X","value":{"lit":1}}]},{"object":[]}]}}]}]})J", true);
        const auto& c0 = *world.Components[0];
        check(r.ok && c0.RelativeLocation.X == 0 && c0.RelativeLocation.Y == 2 && c0.RelativeLocation.Z == 0 && c0.Path.size() == 2 &&
                  c0.Path[0].X == 1 && c0.Path[1].X == 0,
              "parity: object literals construct structs (unnamed members keep defaults), also inside lists");
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Components"]},"stages":[{"op":"first","n":1},{"op":"set","assign":[{"target":{"path":["RelativeLocation"]},"value":{"object":[{"key":"W","value":{"lit":2}}]}}]}]})J", true);
        check(!r.ok && first_code(r) == "coercion-failed" && r.diagnostics[0].message.find("no member 'W'") != std::string::npos,
              "parity: object literal with an unknown member → coercion-failed");
        r = arun(ue, R"J({"rql":"0.1","source":{"path":["w","Components"]},"stages":[{"op":"first","n":1},{"op":"set","assign":[{"target":{"path":["Material"]},"value":{"object":[{"key":"Roughness","value":{"lit":0.1}}]}}]}]})J", true);
        check(!r.ok && first_code(r) == "coercion-failed" && r.diagnostics[0].message.find("cannot construct") != std::string::npos,
              "parity: non-constructible object type → coercion-failed");
    }

    // constructed objects stored through raw pointers stay alive (engine-owned); dry-run constructs nothing lasting
    {
        auto gc = conformance::load_graph(graph_path);
        rql::Engine ce(reg);
        ce.add_object_source("g", gc.get());
        ce.mutations().enabled = true;
        auto r = arun(ce, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#8"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Peer"]},"value":{"object":[{"key":"Name","value":{"lit":"made"}},{"key":"Children","value":{"list":[{"object":[{"key":"Name","value":{"lit":"kid"}}]}]}}]}}]}]})J", true);
        conformance::Node* peer = gc->Nodes[2]->Peer;
        check(r.ok && peer && peer->Name == "made" && peer->Id == 0 && peer->Children.size() == 1 && peer->Children[0]->Name == "kid" &&
                  !peer->Note && peer->Tags.empty() && peer->Parent == nullptr,
              "parity: constructed Node defaults + nested construction into shared_ptr children");
        r = arun(ce, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#8"}]},"stages":[{"op":"select","fields":[{"name":"n","expr":{"path":["Peer","Children",{"index":0},"Name"]}}]}]})J");
        check(r.ok && (*r.rows)[0][0].as_string() == "kid", "parity: constructed objects stay alive after the query");
    }

    // review: map entry owner, construction policy, dead intermediates, exact ref, index safety
    {
        auto gr = conformance::load_graph(graph_path);
        rql::Engine re(reg);
        re.add_object_source("g", gr.get());
        re.mutations().enabled = true;
        conformance::register_test_adapters(re, reg);
        std::vector<rql::MutationRecord> recs;
        re.mutations().before_write = [&](const std::vector<rql::MutationRecord>& rr) { recs = rr; };
        auto r = arun(re, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#1"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Props",{"key":"hp"}]},"value":{"lit":1}}]}]})J", true);
        check(r.ok && recs.size() == 1 && recs[0].owner.ptr() == gr->Nodes[0].get(), "review: owner of a map entry is the owning object");
        re.mutations().before_write = nullptr;
        re.mutations().can_write = [](const rql::TypeDescriptor& t, const rql::MemberDescriptor& m) { return !(t.name == "Node" && m.name == "Name"); };
        r = arun(re, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#8"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Peer"]},"value":{"object":[{"key":"Name","value":{"lit":"x"}}]}}]}]})J", true);
        check(!r.ok && first_code(r) == "permission-denied" && gr->Nodes[2]->Peer == nullptr, "review: object-literal construction checks the host policy");
        re.mutations().can_write = nullptr;
        // dead intermediate: Node#8.Peer -> Node#5, then Node#5 is destroyed
        r = arun(re, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#8"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Peer"]},"value":{"call":"ref","args":[{"lit":"Node#5"}]}}]}]})J", true);
        gr->Nodes[1]->Tags.push_back("destroyed");
        r = arun(re, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#8"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Peer","Name"]},"value":{"lit":"x"}}]}]})J", true);
        check(!r.ok && first_code(r) == "coercion-failed" && r.diagnostics[0].message.find("is null") != std::string::npos && gr->Nodes[1]->Name == "root-b",
              "review: a destroyed intermediate container fails as null, nothing written");
        r = arun(re, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#1"}]},"stages":[{"op":"select","fields":[{"name":"i","expr":{"call":"insert","args":[{"path":["Tags"]},{"lit":1e300},{"lit":"x"}]}},{"name":"r","expr":{"call":"remove-at","args":[{"path":["Tags"]},{"lit":-1e300}]}}]}]})J");
        check(r.ok && (*r.rows)[0][0].find("$error") && (*r.rows)[0][1].find("$error"), "review: huge insert / remove-at indexes are type-mismatch (no UB)");
    }
    {
        // exact ref match is final, also through resolve_stable_ref: a destroyed exact match is stale-ref
        auto gx = conformance::load_graph(graph_path);
        rql::Engine xe(reg);
        xe.add_object_source("g", gx.get());
        conformance::Node* lonely = gx->Nodes[2].get();
        conformance::Node* rootb = gx->Nodes[1].get();
        xe.resolve_stable_ref = [&](const std::string&, const std::string& id) {
            if (id == "8 lonely") return reg.wrap(lonely);
            if (id == "8") return reg.wrap(rootb);
            return rql::Value::missing();
        };
        xe.is_alive = [&](const rql::Value& v) { return v.ptr() != lonely; };
        auto r = arun(xe, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#8 lonely"}]},"stages":[{"op":"count"}]})J");
        check(!r.ok && first_code(r) == "stale-ref", "review: a destroyed exact ref match is final (no display-form fallback)");
        xe.is_alive = nullptr;
        r = arun(xe, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#8 (copied)"}]},"stages":[{"op":"select","fields":[{"name":"n","expr":{"path":["Name"]}}]}]})J");
        check(r.ok && (*r.rows)[0][0].as_string() == "root-b", "review: an absent exact id still falls back to the display form");
    }

    // ownership of literal-built objects: retained by the engine until the host releases them
    {
        auto go = conformance::load_graph(graph_path);
        rql::Engine oe(reg);
        oe.add_object_source("g", go.get());
        oe.mutations().enabled = true;
        const char* set_peer = R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#8"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Peer"]},"value":{"object":[{"key":"Name","value":{"lit":"made"}}]}}]}]})J";
        bool all_ok = true;
        for (int i = 0; i < 5; ++i) all_ok = arun(oe, set_peer, true).ok && all_ok;
        // Node#5.Peer := Node#8.Peer (a raw copy of the link): every earlier object must still be alive
        auto r = arun(oe, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#5"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Peer"]},"value":{"path":["Peer"],"on":{"call":"ref","args":[{"lit":"Node#8"}]}}}]}]})J", true);
        r = arun(oe, set_peer, true);
        r = arun(oe, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#5"}]},"stages":[{"op":"select","fields":[{"name":"n","expr":{"path":["Peer","Name"]}}]}]})J");
        check(all_ok && r.ok && (*r.rows)[0][0].as_string() == "made" && oe.retained_literal_objects() == 6,
              "parity: literal-built objects are retained (never freed automatically), raw links stay valid");
        run(oe, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"set","assign":[{"target":{"path":["Peer"]},"value":{"lit":null}}]}]})J", true, true);
        size_t dry = oe.retained_literal_objects();
        run(oe, set_peer, true, true);  // dry run: builds an object but retains nothing
        go->Nodes[1]->Peer = nullptr;
        go->Nodes[2]->Peer = nullptr;
        oe.release_literal_objects();  // the host knows nothing links to them anymore
        check(dry == 6 && oe.retained_literal_objects() == 0, "parity: dry runs retain nothing; release_literal_objects frees on request");
    }
    {
        // host factory (Unreal NewObject seam): host-owned objects are never retained by the engine
        struct HostNodePool {
            std::vector<std::unique_ptr<conformance::Node>> owned;
        };
        static HostNodePool pool;
        rql::TypeRegistry hr;
        conformance::register_types(hr);
        hr.object<conformance::Node>("Node").factory([] {
            pool.owned.push_back(std::make_unique<conformance::Node>());
            return pool.owned.back().get();
        });
        auto gh = conformance::load_graph(graph_path);
        rql::Engine he(hr);
        he.add_object_source("g", gh.get());
        he.mutations().enabled = true;
        auto r = arun(he, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#8"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Peer"]},"value":{"object":[{"key":"Name","value":{"lit":"hosted"}}]}}]}]})J", true);
        check(r.ok && pool.owned.size() == 1 && gh->Nodes[2]->Peer == pool.owned[0].get() && gh->Nodes[2]->Peer->Name == "hosted" &&
                  he.retained_literal_objects() == 0,
              "parity: host-factory objects are host-owned (not retained by the engine)");
    }

    // configuration bugs → internal-error
    {
        auto g2 = conformance::load_graph(graph_path);
        rql::Engine bad(reg);
        bad.add_object_source("g", g2.get());
        rql::TypeAdapter a;
        a.target = reg.descriptor<conformance::Node>();
        a.configure_members = [](rql::MemberTable& t) { t.read_only({"NoSuchMember"}); };
        bad.add_adapter(std::move(a));
        auto r = arun(bad, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"select","fields":[{"name":"n","expr":{"path":["Name"]}}]}]})J");
        check(!r.ok && first_code(r) == "internal-error", "adapters: configuring a missing member → internal-error");
        rql::Engine bad2(reg);
        bad2.add_object_source("g", g2.get());
        rql::TypeAdapter b;
        b.target = reg.descriptor<conformance::Vec2>();
        b.configure_members = [](rql::MemberTable& t) {
            t.add("y2", rql::builtin::double_type(), [](void*) { return rql::Value::number(0); }, [](void*, const rql::Value&) {});
        };
        bad2.add_adapter(std::move(b));
        r = arun(bad2, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"select","fields":[{"name":"p","expr":{"path":["Pos","y2"]}}]}]})J");
        check(!r.ok && first_code(r) == "internal-error", "adapters: virtual setter on a value type rejected (internal-error)");
    }
}

// ------------------------------------------------------------------ semantics port (math, suggestions, receipts, codes)

struct PBody {
    int Id = 0;
    std::string Label;
    std::string Type;
    double Mass = 1;
};
struct PWide {
    int Id = 0;
};
struct PDoor {
    int Id = 0;
    bool Locked = false;
    int Opened = 0;
    void Open(int force) { Opened += force; }
};
struct PPad {
    virtual ~PPad() = default;
    long long pad[4] = {111, 222, 333, 444};
};
struct PBigDoor : PPad, PDoor {};  // PDoor at a non-zero offset
struct PBigWorld {
    std::vector<std::unique_ptr<PBigDoor>> Doors;
};
struct PWorld {
    std::vector<std::unique_ptr<PBody>> Bodies;
    PWide Wide;
    std::vector<std::unique_ptr<PDoor>> Doors;
};

bool has_diag(const rql::QueryResult& r, const std::string& code, rql::Severity sev, size_t* count = nullptr) {
    size_t n = 0;
    for (auto& d : r.diagnostics)
        if (d.code == code && d.severity == sev) ++n;
    if (count) *count = n;
    return n > 0;
}

std::string hint_of(const rql::QueryResult& r, const std::string& code) {
    for (auto& d : r.diagnostics)
        if (d.code == code) return d.hint;
    return "";
}

void port_tests() {
    // shortest round-trip single-precision formatting; non-BMP characters round-trip through JSON
    check(rql::format_float(static_cast<float>(3.14159265358979323846 / 2)) == "1.5707964", "float: (float)(pi/2) → 1.5707964");
    check(rql::format_float(136.0f / 255.0f) == "0.53333336", "float: 136/255f → 0.53333336");
    check(rql::format_float(0.1f) == "0.1", "float: 0.1f → 0.1");
    const std::string emoji = "a\xF0\x9F\x98\x80" "b";
    check(Json::parse(Json(emoji).dump()).as_string() == emoji, "json: emoji round-trips through dump/parse");

    rql::TypeRegistry reg;
    reg.object<PBody>("PBody")
        .member("Id", &PBody::Id)
        .member("Label", &PBody::Label)
        .member("Type", &PBody::Type)
        .property("Mass", [](PBody& b) { return b.Mass; }, [](PBody& b, double v) { b.Mass = v < 1e-7 ? 1e-7 : v; });  // clamps
    auto wide = reg.object<PWide>("PWide");
    wide.member("Id", &PWide::Id);
    for (int i = 0; i < 15; ++i) wide.property("m" + std::to_string(i), [i](PWide&) { return i; });
    reg.object<PDoor>("PDoor")
        .member("Id", &PDoor::Id)
        .member("Locked", &PDoor::Locked)
        .member("Opened", &PDoor::Opened)
        .method("Open", &PDoor::Open, {"force"})
        .validate_call([](PDoor& d, const std::vector<rql::Value>& args) -> std::string {
            if (d.Locked) return "door " + std::to_string(d.Id) + " is locked";
            if (args[0].as_number() > 10) throw std::runtime_error("force gauge broken");
            return "";
        });
    reg.object<PWorld>("PWorld").member("Bodies", &PWorld::Bodies).member("Wide", &PWorld::Wide).member("Doors", &PWorld::Doors);
    PWorld w;
    for (int i = 1; i <= 2; ++i) {
        auto d = std::make_unique<PDoor>();
        d->Id = i;
        d->Locked = i == 2;
        w.Doors.push_back(std::move(d));
    }
    for (int i = 1; i <= 2; ++i) {
        auto b = std::make_unique<PBody>();
        b->Id = i;
        b->Label = i == 1 ? "zeta" : "alpha";
        b->Mass = 4.0 * i;
        w.Bodies.push_back(std::move(b));
    }
    rql::Engine e(reg);
    e.add_object_source("w", &w);
    e.mutations().enabled = true;

    // math functions (results are doubles; NaN propagates through min / max like .NET Math.Min / Max)
    auto r = run(e, R"J({"rql":"0.1","source":{"path":["w"]},"stages":[{"op":"select","fields":[
        {"name":"p","expr":{"call":"pow","args":[{"lit":2},{"lit":10}]}},
        {"name":"lo","expr":{"call":"min","args":[{"lit":3},{"lit":1},{"lit":2}]}},
        {"name":"hi","expr":{"call":"max","args":[{"lit":3},{"lit":7.5}]}},
        {"name":"n1","expr":{"call":"min","args":[{"call":"sqrt","args":[{"lit":-1}]},{"lit":1}]}},
        {"name":"n2","expr":{"call":"max","args":[{"lit":1},{"call":"sqrt","args":[{"lit":-1}]}]}},
        {"name":"bad","expr":{"call":"min","args":[{"lit":1},{"lit":"x"}]}},
        {"name":"nul","expr":{"call":"max","args":[{"lit":1},{"lit":null}]}}]}]})J");
    bool math_ok = r.ok && r.rows && (*r.rows)[0][0].as_number() == 1024 && (*r.rows)[0][1].as_number() == 1 &&
                   (*r.rows)[0][2].as_number() == 7.5 && (*r.rows)[0][3].is_string() && (*r.rows)[0][3].as_string() == "NaN" &&
                   (*r.rows)[0][4].is_string() && (*r.rows)[0][4].as_string() == "NaN" && (*r.rows)[0][5].find("$error") &&
                   (*r.rows)[0][5].find("$error")->as_string() == "type-mismatch" && (*r.rows)[0][6].is_null();
    check(math_ok, "math: pow / min / max / NaN / type-mismatch / null");
    r = run(e, R"J({"rql":"0.1","source":{"path":["w"]},"stages":[{"op":"select","fields":[{"name":"a","expr":{"call":"min","args":[{"lit":1}]}}]}]})J");
    check(!r.ok && first_code(r) == "arity", "math: min with one argument → arity");

    // suggestions: optimal string alignment and containment rules
    r = run(e, R"J({"rql":"0.1","source":{"path":["w","Bodies"]},"stages":[{"op":"select","fields":[{"name":"x","expr":{"path":["Lable"]}}]}]})J");
    check(r.ok && hint_of(r, "unknown-member").find("did you mean Label?") == 0, "suggest: Lable → Label (transposition = 1)");
    r = run(e, R"J({"rql":"0.1","source":{"path":["w","Bodies"]},"stages":[{"op":"select","fields":[{"name":"x","expr":{"path":["NoSuchType"]}}]}]})J");
    check(r.ok && hint_of(r, "unknown-member").find("did you mean") == std::string::npos, "suggest: NoSuchType does not suggest Type");
    r = run(e, R"J({"rql":"0.1","source":{"path":["w","Wide"]},"stages":[{"op":"select","fields":[{"name":"x","expr":{"path":["Qqqq"]}}]}]})J");
    {
        std::string h = hint_of(r, "unknown-member");
        check(r.ok && h.find("members: Id m0 ") == 0 && h.find("m10 \xE2\x80\xA6 (4 more)") != std::string::npos && h.find("m11") == std::string::npos,
              "suggest: member list capped at 12 names + … (N more)");
    }

    // a dotted path names a projected column of that name
    r = run(e, R"J({"rql":"0.1","source":{"path":["w","Bodies"]},"stages":[{"op":"select","fields":[{"name":"Owner.Label","expr":{"path":["Label"]}}]},{"op":"sort","keys":[{"expr":{"path":["Owner","Label"]}}]}]})J");
    check(r.ok && r.diagnostics.empty() && (*r.rows)[0][0].as_string() == "alpha" && (*r.rows)[1][0].as_string() == "zeta",
          "select: dotted path resolves to the dotted column");

    // matches: invalid literal → error before running; computed invalid → one warning
    r = run(e, R"J({"rql":"0.1","source":{"path":["w","Bodies"]},"stages":[{"op":"where","pred":{"op":"matches","args":[{"path":["Label"]},{"lit":"(["}]}},{"op":"count"}]})J");
    check(!r.ok && first_code(r) == "invalid-regex" && r.diagnostics[0].message.find("([") != std::string::npos, "matches: invalid literal regex → invalid-regex");
    r = run(e, R"J({"rql":"0.1","source":{"path":["w","Bodies"]},"stages":[{"op":"where","pred":{"op":"matches","args":[{"path":["Label"]},{"op":"+","args":[{"path":["Label"]},{"lit":"["}]}]}},{"op":"count"}]})J");
    size_t n = 0;
    check(r.ok && has_diag(r, "invalid-regex", rql::Severity::Warning, &n) && n == 1, "matches: computed invalid regex → one warning");

    // aggregates report ignored values once
    r = run(e, R"J({"rql":"0.1","source":{"path":["w","Bodies"]},"stages":[{"op":"sum","expr":{"path":["Label"]}}]})J");
    check(r.ok && r.value.as_number() == 0 && has_diag(r, "type-mismatch", rql::Severity::Warning, &n) && n == 1 &&
              r.diagnostics[0].message.find("sum ignored 2 non-numeric value(s)") == 0,
          "aggregate: sum of strings warns once");

    // extension function that throws: error value + one warning; as the source the query fails once
    e.add_function("app.find", [](const std::vector<rql::Value>&) -> rql::Value { throw std::runtime_error("no such object"); });
    r = run(e, R"J({"rql":"0.1","source":{"path":["w","Bodies"]},"stages":[{"op":"select","fields":[{"name":"f","expr":{"call":"find","on":{"path":["app"]},"args":[{"path":["Label"]}]}}]}]})J");
    check(r.ok && (*r.rows)[0][0].find("$error") && (*r.rows)[0][0].find("$error")->as_string() == "function-threw" &&
              has_diag(r, "function-threw", rql::Severity::Warning, &n) && n == 1 &&
              r.diagnostics[0].message == "app.find() threw: no such object",
          "function-threw: error value + one warning");
    r = run(e, R"J({"rql":"0.1","source":{"call":"find","on":{"path":["app"]},"args":[{"lit":"x"}]},"stages":[{"op":"count"}]})J");
    check(!r.ok && r.diagnostics.size() == 1 && first_code(r) == "function-threw", "function-threw: source function fails the query once");

    // a mutation request must end in set / call
    r = run(e, R"J({"rql":"0.1","source":{"path":["w","Bodies"]},"stages":[{"op":"count"}]})J", true);
    check(!r.ok && first_code(r) == "invalid-plan" && w.Bodies[0]->Mass == 4, "mutation request without set/call → invalid-plan");

    // receipts read the stored value back (the setter clamps); dry runs keep the planned value; hooks see read-back
    double hooked = 0;
    e.mutations().committed = [&](const std::vector<rql::MutationRecord>& recs) { hooked = recs.empty() ? 0 : recs[0].new_value.as_number(); };
    const char* clamp = R"J({"rql":"0.1","source":{"path":["w","Bodies"]},"stages":[{"op":"first","n":1},{"op":"set","assign":[{"target":{"path":["Mass"]},"value":{"lit":-5}}]}]})J";
    r = run(e, clamp, true, true);
    check(r.ok && r.dry_run && (*r.rows)[0][3].as_number() == -5 && w.Bodies[0]->Mass == 4, "receipt: dry run shows the planned value");
    r = run(e, clamp, true);
    check(r.ok && (*r.rows)[0][3].as_number() == 1e-7 && w.Bodies[0]->Mass == 1e-7 && hooked == 1e-7,
          "receipt: new is the read-back (clamped) value, also for the committed hook");
    e.mutations().committed = nullptr;

    // call validators: every target is checked before the first call, on dry runs too (spec §8)
    r = run(e, R"J({"rql":"0.1","source":{"path":["w","Doors"]},"stages":[{"op":"call","method":"Open","args":[{"lit":1}]}]})J", true);
    check(!r.ok && first_code(r) == "invalid-value" && r.diagnostics[0].message.find("door 2 is locked; nothing was called") != std::string::npos &&
              w.Doors[0]->Opened == 0 && w.Doors[1]->Opened == 0,
          "call validator: one rejected target → nothing called");
    const char* force = R"J({"rql":"0.1","source":{"path":["w","Doors"]},"stages":[{"op":"first","n":1},{"op":"call","method":"Open","args":[{"lit":20}]}]})J";
    r = run(e, force, true, true);
    check(!r.ok && first_code(r) == "invalid-value" && r.diagnostics[0].message.find("its check threw: force gauge broken") != std::string::npos,
          "call validator: dry run reports the refusal (throwing check)");
    r = run(e, R"J({"rql":"0.1","source":{"path":["w","Doors"]},"stages":[{"op":"first","n":1},{"op":"call","method":"Open","args":[{"lit":2}]}]})J", true);
    check(r.ok && w.Doors[0]->Opened == 2, "call validator: accepted call runs");

    // aggregates count error values as ignored (only null / missing are skipped silently)
    r = run(e, R"J({"rql":"0.1","source":{"path":["w","Bodies"]},"stages":[{"op":"sum","expr":{"op":"/","args":[{"path":["Mass"]},{"lit":0}]}}]})J");
    check(r.ok && r.value.as_number() == 0 && has_diag(r, "type-mismatch", rql::Severity::Warning, &n) && n == 1 &&
              r.diagnostics[0].message.find("(first: error(div-by-zero))") != std::string::npos,
          "aggregate: error values are counted as ignored");

    // inherited methods keep their validator, upcast to a base at a non-zero offset
    {
        rql::TypeRegistry reg2;
        reg2.object<PDoor>("PDoor")
            .member("Id", &PDoor::Id)
            .member("Locked", &PDoor::Locked)
            .member("Opened", &PDoor::Opened)
            .method("Open", &PDoor::Open, {"force"})
            .validate_call([](PDoor& d, const std::vector<rql::Value>&) -> std::string { return d.Locked ? "locked" : ""; });
        reg2.object<PBigDoor>("PBigDoor").base<PDoor>();
        reg2.object<PBigWorld>("PBigWorld").member("Doors", &PBigWorld::Doors);
        PBigWorld bw;
        for (int i = 1; i <= 2; ++i) {
            auto d = std::make_unique<PBigDoor>();
            d->Id = i;
            d->Locked = i == 2;
            bw.Doors.push_back(std::move(d));
        }
        rql::Engine e2(reg2);
        e2.add_object_source("w", &bw);
        e2.mutations().enabled = true;
        r = run(e2, R"J({"rql":"0.1","source":{"path":["w","Doors"]},"stages":[{"op":"call","method":"Open","args":[{"lit":1}]}]})J", true);
        check(!r.ok && first_code(r) == "invalid-value" && bw.Doors[0]->Opened == 0 && bw.Doors[1]->Opened == 0,
              "call validator: inherited through base<B>() at a non-zero offset");
        r = run(e2, R"J({"rql":"0.1","source":{"path":["w","Doors"]},"stages":[{"op":"first","n":1},{"op":"call","method":"Open","args":[{"lit":3}]}]})J", true);
        check(r.ok && bw.Doors[0]->Opened == 3, "call validator: inherited validator accepts the unlocked door");
    }
}

}  // namespace

/// Wave-2 findings: budget hints, host hint overrides, batch validation, ref() and list-index hints.
void wave2_tests() {
    rql::TypeRegistry reg;
    conformance::register_types(reg);
    auto g = conformance::load_graph(repo_root + "/fixtures/cross-runtime/graph.json");
    auto engine = [&](rql::Engine& e) {
        e.add_object_source("g", g.get());
        e.mutations().enabled = true;
        e.mutations().max_targets = 2;
        e.mutations().hard_max_targets = 30;
    };
    const std::string set_all = R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"set","assign":[{"target":{"path":["Value"]},"value":{"lit":1}}]}]})J";
    auto hint = [](const rql::QueryResult& r) { return r.diagnostics.empty() ? std::string() : r.diagnostics[0].hint; };
    auto has = [](const std::string& s, const std::string& part) { return s.find(part) != std::string::npos; };
    for (bool adjustable : {true, false}) {
        rql::Engine e(reg);
        engine(e);
        e.defaults().budgets_adjustable = adjustable;
        auto r = arun(e, set_all, true);
        std::string h = hint(r);
        check(first_code(r) == "mutation-limit" && has(h, "first 2 | set") && has(h, "skip 2 | first 2 | set") &&
                  has(h, "budgets.maxMutationTargets (host ceiling 30)") == adjustable && (adjustable || !has(h, "budgets")),
              adjustable ? "mutation-limit hint: batches + budgets when adjustable" : "mutation-limit hint: no budgets when not adjustable");
        rql::Budgets b;
        b.max_visited = 2;
        auto v = e.execute_json(R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"count"}]})J", b, {});
        check(first_code(v) == "budget-exceeded" && has(hint(v), "budgets.maxVisited") == adjustable, "budget-exceeded hint follows budgets_adjustable");
    }
    {
        rql::Engine e(reg);
        engine(e);
        e.mutations().limit_hint = [](const std::string& op, int limit) { return "pass max_targets (now " + std::to_string(limit) + ") per " + op; };
        check(hint(arun(e, set_all, true)) == "pass max_targets (now 2) per set", "host replaces the mutation-limit hint");
    }
    {
        rql::Engine e(reg);
        engine(e);
        e.mutations().max_targets = 20;
        std::vector<rql::MutationRecord> seen;
        e.mutations().validate_batch = [&](const std::vector<rql::MutationRecord>& b) -> std::string {
            seen = b;
            throw std::runtime_error("nope");
        };
        rql::QueryOptions o;
        o.allow_mutation = true;
        o.dry_run = true;
        auto r = e.execute_json(R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"set","assign":[{"target":{"path":["Value"]},"value":{"op":"+","args":[{"path":[{"meta":"index"}]},{"lit":5}]}}]}]})J", {}, o);
        check(first_code(r) == "invalid-value" && has(r.diagnostics[0].message, "nope") && seen.size() == 3 && seen[2].member == "Value" &&
                  seen[2].new_value.as_number() == 7 && seen[2].args.empty() && g->Nodes[0]->Value == 10,
              "batch validator: every change, dry run, a throw refuses");
    }
    {
        rql::Engine e(reg);
        engine(e);
        auto ref = R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Floor"}]},"stages":[{"op":"count"}]})J";
        check(has(hint(arun(e, ref)), "not a name") && has(hint(arun(e, ref)), "look it up by name"), "ref() of a name: generic hint");
        e.find_hint = "unreal.find(\"Floor\")";
        check(has(hint(arun(e, ref)), "look it up with unreal.find(\"Floor\")"), "ref() of a name: host finder in the hint");
        auto r = arun(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#8"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Tags",{"index":0}]},"value":{"lit":"x"}}]}]})J", true);
        check(first_code(r) == "invalid-plan" && has(r.diagnostics[0].message, "index 0 out of range (count 0)") && has(hint(r), "set Tags = append(Tags, value)"),
              "out-of-range list index: hint to assign the whole list");
    }
    {
        // U5: a set through streamed map entries reports the object the map was read from as the owner
        rql::Engine e(reg);
        engine(e);
        e.mutations().max_targets = 20;
        std::vector<rql::MutationRecord> before;
        e.mutations().before_write = [&](const std::vector<rql::MutationRecord>& b) { before = b; };
        auto r = arun(e, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"get","expr":{"path":["Props"]}},{"op":"where","pred":{"op":">=","args":[{"path":["Value"]},{"lit":50}]}},{"op":"set","assign":[{"target":{"path":["Value"]},"value":{"op":"*","args":[{"path":["Value"]},{"lit":2}]}}]}]})J", true);
        check(r.ok && before.size() == 2 && before[0].member == "Props[\"hp\"]" && before[0].owner.ptr() == g->Nodes[0].get() &&
                  before[1].owner.ptr() == g->Nodes[1].get() && g->Nodes[0]->Props["hp"] == 200,
              "map-entry set: owner is the object the map was read from");
    }
    {
        // UTF-8: positions count UTF-16 units like len; byte-wise regex matches never split a character
        rql::Engine e(reg);
        engine(e);
        auto r = arun(e, R"J({"rql":"0.1","source":{"path":["g"]},"stages":[{"op":"select","fields":[{"name":"a","expr":{"call":"substring","args":[{"lit":"héllo"},{"lit":1},{"lit":3}]}},{"name":"b","expr":{"call":"substring","args":[{"lit":"a😀b"},{"lit":1},{"lit":2}]}},{"name":"c","expr":{"call":"replace-regex","args":[{"lit":"é"},{"lit":"x*"},{"lit":"-"}]}},{"name":"d","expr":{"call":"pad","args":[{"lit":"é"},{"lit":3}]}}]}]})J");
        check(r.ok && (*r.rows)[0][0].as_string() == "\xC3\xA9ll" && (*r.rows)[0][1].as_string() == "\xF0\x9F\x98\x80" &&
                  (*r.rows)[0][2].as_string() == "-\xC3\xA9-" && (*r.rows)[0][3].as_string() == "00\xC3\xA9",
              "string functions: UTF-16 positions over UTF-8 text, regex matches keep characters whole");
    }
    {
        // U6: host text coercion for a struct no adapter handles
        rql::Engine e(reg);
        engine(e);
        rql::TypeRegistry* rp = &reg;
        const std::string plan = R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#8"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Pos"]},"value":{"lit":"X=3|Y=4"}}]}]})J";
        check(first_code(arun(e, plan, true)) == "coercion-failed", "text coercion: without the hook a string is no struct");
        e.mutations().coerce_text = [rp](const rql::TypeDescriptor& t, const std::string& text, std::string& reason) -> std::optional<rql::Value> {
            if (t.name != "Vec2") return std::nullopt;
            conformance::Vec2 v;
            size_t bar = text.find("|Y=");
            if (text.rfind("X=", 0) != 0 || bar == std::string::npos) {
                reason = "expected X=..|Y=..";
                return std::nullopt;
            }
            v.x = std::strtod(text.substr(2, bar - 2).c_str(), nullptr);
            v.y = std::strtod(text.substr(bar + 3).c_str(), nullptr);
            return rql::Traits<conformance::Vec2>::get(*rp, v);
        };
        auto r = arun(e, plan, true);
        check(r.ok && g->Nodes[2]->Pos.x == 3 && g->Nodes[2]->Pos.y == 4, "text coercion: the host converts its text form");
        auto bad = arun(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#8"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Pos"]},"value":{"lit":"nope"}}]}]})J", true);
        check(first_code(bad) == "coercion-failed" && has(bad.diagnostics[0].message, "expected X=..|Y=.."), "text coercion: a refusal is coercion-failed with the reason");
    }
}

int main(int argc, char** argv) {
    if (argc > 1) repo_root = argv[1];
    try {
        json_tests();
        codec_tests();
        engine_tests();
        adapter_tests();
        port_tests();
        wave2_tests();
    } catch (const std::exception& ex) {
        std::cout << "FATAL " << ex.what() << "\n";
        return 2;
    }
    std::cout << passed << " passed, " << failed << " failed, 0 skipped\n";
    return failed == 0 ? 0 : 1;
}
