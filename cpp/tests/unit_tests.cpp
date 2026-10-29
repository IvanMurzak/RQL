// Unit tests: JSON edge cases, number formatting, and engine features the golden vectors do not
// cover (call stage, extensions, session handles, liveness, unordered maps, shared_ptr, rollback).
#include <cmath>
#include <cstdlib>
#include <fstream>
#include <iterator>
#include <memory>
#include <iostream>
#include <limits>
#include <map>
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

    // a request maxMutationTargets above the host ceiling is refused, never clamped (spec §8)
    e.mutations().hard_max_targets = 2;
    {
        rql::Budgets mb;
        mb.max_mutation_targets = 100;
        rql::QueryOptions mo;
        mo.allow_mutation = true;
        r = e.execute_json(R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"set","assign":[{"target":{"path":["Count"]},"value":{"lit":1}}]}]})", mb, mo);
        check(!r.ok && first_code(r) == "invalid-value" && r.diagnostics[0].message.find("must be from 1 to 2") != std::string::npos,
              "hard_max_targets refuses a larger request budget");
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
        const std::string literal_peer =
            R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#8"}]},"stages":[{"op":"set","assign":[{"target":{"path":["Peer"]},"value":{"object":[{"key":"Name","value":{"lit":"x"}}]}}]}]})J";
        // Node has a liveness adapter here, so it is an engine object: literals never build it unless the host allows it.
        r = arun(re, literal_peer, true);
        check(!r.ok && first_code(r) == "coercion-failed" && r.diagnostics[0].message.find("engine object") != std::string::npos && gr->Nodes[2]->Peer == nullptr,
              "overloads: an engine object (liveness adapter) is never built from an object literal");
        re.mutations().can_construct_from_literal = [](const rql::TypeDescriptor& t) -> std::optional<bool> {
            if (t.name == "Node") return true;
            return std::nullopt;
        };
        r = arun(re, literal_peer, true);
        check(!r.ok && first_code(r) == "permission-denied" && gr->Nodes[2]->Peer == nullptr, "review: object-literal construction checks the host policy");
        re.mutations().can_construct_from_literal = nullptr;
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
        // Ranked by similarity to the name (all equally far here), then ordinal; capped at 12.
        check(r.ok && h.find("members: Id m0 m1 m10 m11 m12 m13 m14 m2 m3 m4 m5 \xE2\x80\xA6 (4 more)") == 0,
              "suggest: member list capped at 12 names + … (N more), by similarity");
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
        // 3 roots, limit 2, ceiling 30: the count is exact.
        check(first_code(r) == "mutation-limit" && r.diagnostics[0].message == "3 objects matched 'set', more than the limit of 2; nothing was changed",
              "mutation-limit message: the matched count");
        check(adjustable ? h == "pass budgets.maxMutationTargets = 3 (host ceiling 30) to change all 3 in one all-or-nothing call (a dry run previews it); "
                                "or narrow the query (where / ref(...))"
                         : has(h, "first 2 | set") && has(h, "skip 2 | first 2 | set") && !has(h, "budgets"),
              adjustable ? "mutation-limit hint: raise to the count when adjustable" : "mutation-limit hint: batches, no budgets when not adjustable");
        {
            // A host limit argument is advised whatever budgets_adjustable says; more than the ceiling → batches of it.
            rql::Engine e2(reg);
            engine(e2);
            e2.defaults().budgets_adjustable = adjustable;
            e2.mutations().limit_argument = "max_targets";
            e2.mutations().hard_max_targets = 4;
            auto many = arun(e2, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"recurse","via":[{"path":["Children"]}]},{"op":"set","assign":[{"target":{"path":["Value"]},"value":{"lit":1}}]}]})J", true);
            check(first_code(many) == "mutation-limit" && many.diagnostics[0].message == "more than 4 objects matched 'set' (the limit is 2); nothing was changed" &&
                      hint(many).rfind("pass max_targets = 4 (the host ceiling) and split the change into batches of 4", 0) == 0,
                  "mutation-limit hint: host limit argument, batches of the ceiling");
            rql::Budgets raise;
            raise.max_mutation_targets = 3;
            rql::QueryOptions o;
            o.allow_mutation = true;
            o.dry_run = true;  // the graph is shared by the following checks
            check(e2.execute_json(set_all, raise, o).ok, "a request raises the target limit whatever budgets_adjustable says");
            rql::Budgets tight;
            tight.max_visited = 6;  // enough to collect the first 3 targets, not to count all 8 nodes
            e2.mutations().hard_max_targets = 30;
            auto budget = e2.execute_json(R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"recurse","via":[{"path":["Children"]}]},{"op":"set","assign":[{"target":{"path":["Value"]},"value":{"lit":1}}]}]})J", tight, o);
            check(first_code(budget) == "budget-exceeded", "a budget error while counting is reported instead of mutation-limit");
        }
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

/// Wave-3 findings: enum name resolution (spec §8) with host aliases.
enum class EAlign { HAlign_Fill, HAlign_Left, VAlign_Fill, VAlign_Top, Center };
struct Widget {
    EAlign Align = EAlign::HAlign_Left;
    std::map<EAlign, int> Weights{{EAlign::VAlign_Top, 1}};
};

void wave3_tests() {
    rql::TypeRegistry reg;
    reg.enumeration<EAlign>("EAlign")
        .value("HAlign_Fill", EAlign::HAlign_Fill)
        .value("HAlign_Left", EAlign::HAlign_Left)
        .value("VAlign_Fill", EAlign::VAlign_Fill)
        .value("VAlign_Top", EAlign::VAlign_Top)
        .value("Center", EAlign::Center);
    reg.object<Widget>("Widget").member("Align", &Widget::Align).member("Weights", &Widget::Weights);
    Widget w;
    rql::Engine e(reg);
    e.add_object_source("w", &w);
    e.mutations().enabled = true;
    auto set = [&](const std::string& literal) {
        return arun(e, R"J({"rql":"0.1","source":{"path":["w"]},"stages":[{"op":"set","assign":[{"target":{"path":["Align"]},"value":{"lit":)J" + literal + "}}]}]}", true);
    };
    auto count = [&](const std::string& literal) {
        auto r = arun(e, R"J({"rql":"0.1","source":{"path":["w"]},"stages":[{"op":"where","pred":{"op":"==","args":[{"path":["Align"]},{"lit":)J" + literal + "}]}},{\"op\":\"count\"}]}");
        return r.ok && r.has_value ? static_cast<int>(r.value.as_number()) : -1;
    };
    const std::pair<const char*, EAlign> cases[] = {
        {"\"VAlign_Top\"", EAlign::VAlign_Top}, {"\"valign_top\"", EAlign::VAlign_Top},   {"\"EAlign::VAlign_Fill\"", EAlign::VAlign_Fill},
        {"\"EWhatever::Left\"", EAlign::HAlign_Left}, {"\"EAlign.center\"", EAlign::Center}, {"\"Top\"", EAlign::VAlign_Top},
        {"\" top \"", EAlign::VAlign_Top},          {"\"3\"", EAlign::VAlign_Top},
    };
    for (auto& c : cases) {
        w.Align = EAlign::HAlign_Left;
        auto r = set(c.first);
        check(r.ok && w.Align == c.second, std::string("enum name resolution: ") + c.first);
    }
    w.Align = EAlign::HAlign_Left;
    auto amb = set("\"fill\"");
    check(first_code(amb) == "coercion-failed" && amb.diagnostics[0].message.find("\"fill\" is ambiguous for EAlign: HAlign_Fill, VAlign_Fill") != std::string::npos &&
              w.Align == EAlign::HAlign_Left,
          "enum name resolution: ambiguous names are refused with the candidates");
    check(first_code(set("\"Bottom\"")) == "coercion-failed", "enum name resolution: unknown name");
    check(count("\"EAlign::Left\"") == 1 && count("\"Fill\"") == 0, "enum comparisons use the same resolution");
    auto key = arun(e, R"J({"rql":"0.1","source":{"path":["w"]},"stages":[{"op":"select","fields":[{"name":"v","expr":{"path":["Weights",{"key":"Top"}]}}]}]})J");
    check(key.ok && (*key.rows)[0][0].as_number() == 1, "enum map keys use the same resolution");
    // Host aliases (EnumBuilder::alias / TypeDescriptor::enum_aliases) resolve like entry names, before the prefix step.
    reg.descriptor<EAlign>()->enum_aliases = {{"Fill", static_cast<int64_t>(EAlign::HAlign_Fill)}, {"Middle", static_cast<int64_t>(EAlign::Center)}};
    check(set("\"Fill\"").ok && w.Align == EAlign::HAlign_Fill, "enum alias decides at the exact step");
    check(set("\"middle\"").ok && w.Align == EAlign::Center && count("\"MIDDLE\"") == 1, "enum alias ignoring case, in comparisons too");
    auto enc = arun(e, R"J({"rql":"0.1","source":{"path":["w"]},"stages":[{"op":"select","fields":[{"name":"Align","expr":{"path":["Align"]}}]}]})J");
    check(enc.ok && (*enc.rows)[0][0].as_string() == "Center", "enum alias: encoding keeps the entry name");
}

struct W3Door {
    std::string Name;
    int Opened = 0;
    void Open(int force) { Opened += force; }
};
struct W3Editor {
    std::string CreateThing(std::string name) { return name; }
};

void wave3b_tests() {
    rql::TypeRegistry reg;
    reg.object<W3Door>("W3Door").member("Name", &W3Door::Name).member("Opened", &W3Door::Opened).method("Open", &W3Door::Open, {"force"});
    reg.object<W3Editor>("W3Editor").method("CreateThing", &W3Editor::CreateThing, {"name"});
    W3Door door;
    door.Name = "X";
    W3Editor editor;
    rql::Engine e(reg);
    e.add_object_source("door", &door);
    e.add_object_source("editor", &editor);
    e.mutations().enabled = true;
    auto has = [](const std::string& s, const std::string& part) { return s.find(part) != std::string::npos; };
    // Unknown source: the example is a registered source, never an invented one.
    auto src = arun(e, R"J({"rql":"0.1","source":{"lit":0},"stages":[]})J");
    check(first_code(src) == "unknown-source" && has(src.diagnostics[0].message, "(e.g. door)") && !has(src.diagnostics[0].message, "world") &&
              has(src.diagnostics[0].hint, "sources: door editor"),
          "unknown-source example names a registered source");
    // no-match: the host's schema tool, neutral by default.
    const std::string is_window = R"J({"rql":"0.1","source":{"path":["door"]},"stages":[{"op":"where","pred":{"op":"is","args":[{"path":[]},{"lit":"Window"}]}}]})J";
    auto no_match_hint = [&]() {
        auto r = arun(e, is_window);
        for (auto& d : r.diagnostics)
            if (d.code == "no-match") return d.hint;
        return std::string();
    };
    check(has(no_match_hint(), "list the types with the schema tool"), "no-match hint: neutral schema tool");
    e.schema_hint = "rql-schema";
    check(has(no_match_hint(), "list the types with rql-schema") && !has(no_match_hint(), "rql_schema"), "no-match hint: host schema tool");
    // A mutation emptied by a typo fails; a name the host knows keeps it ok.
    const std::string typo = R"J({"rql":"0.1","source":{"path":["door"]},"stages":[{"op":"get","expr":{"path":["Nmae"]}},{"op":"set","assign":[{"target":{"path":["Name"]},"value":{"lit":"Y"}}]}]})J";
    auto r = arun(e, typo, true);
    check(!r.ok && first_code(r) == "unknown-member" && r.diagnostics[0].severity == rql::Severity::Error &&
              has(r.diagnostics[0].message, "nothing was changed") && door.Name == "X",
          "a mutation emptied by a typo fails");
    const std::string known = R"J({"rql":"0.1","source":{"path":["door"]},"stages":[{"op":"where","pred":{"op":">","args":[{"path":["intensity"]},{"lit":1}]}},{"op":"set","assign":[{"target":{"path":["Name"]},"value":{"lit":"Y"}}]}]})J";
    check(!arun(e, known, true).ok, "an unknown name nobody declares fails the empty mutation");
    e.is_known_member = [](const std::string&) -> bool { throw std::runtime_error("boom"); };
    check(!arun(e, known, true).ok, "a throwing known-member hook counts as not known");
    const std::string guard = R"J({"rql":"0.1","source":{"path":["door"]},"stages":[{"op":"where","pred":{"call":"has","args":[{"path":["glow"]}]}},{"op":"set","assign":[{"target":{"path":["Name"]},"value":{"lit":"Y"}}]}]})J";
    check(arun(e, guard, true).ok, "has() is a guard, not a typo");
    e.is_known_member = [](const std::string& n) { return n == "intensity"; };
    auto ok = arun(e, known, true);
    check(ok.ok && ok.rows && ok.rows->empty(), "a member the host knows keeps an empty mutation ok");
    // Unknown callable: a source that has the method is named.
    auto call = arun(e, R"J({"rql":"0.1","source":{"path":["door"]},"stages":[{"op":"call","method":"CreateThing","args":[{"lit":"a"}]}]})J", true);
    check(first_code(call) == "unknown-function" && has(call.diagnostics[0].hint, "callable: Open(") &&
              has(call.diagnostics[0].hint, "the source 'editor' has CreateThing: editor | call CreateThing(\xE2\x80\xA6)"),
          "unknown-callable hint names a source that has it");
    // if(): lazy, the untaken branch is never evaluated.
    auto lazy = arun(e, R"J({"rql":"0.1","source":{"path":["door"]},"stages":[{"op":"select","fields":[{"name":"v","expr":{"call":"if","args":[{"lit":true},{"path":["Name"]},{"call":"ref","args":[{"lit":"W3Door#99"}]}]}}]}]})J");
    check(lazy.ok && (*lazy.rows)[0][0].as_string() == "X", "if() is lazy");
}

struct W4Item {
    std::string Name;
    float Weight = 0;
};
struct W4Bag {
    std::vector<std::shared_ptr<W4Item>> Items;
};

void wave4_tests() {
    auto has = [](const std::string& s, const std::string& part) { return s.find(part) != std::string::npos; };
    // Float members widen to the shortest decimal: arithmetic and aggregates show 4.2, not 4.19999980926514.
    check(rql::widen_single(4.2f) == 4.2 && rql::widen_single(0.1f) == 0.1 && rql::widen_single(1.5707964f) == 1.5707964 &&
              rql::widen_single(1e-30f) == 1e-30 && rql::widen_single(123456.79f) == 123456.79 && rql::widen_single(-2.5f) == -2.5 &&
              rql::widen_single(123456789.0f) == 123456790.0 && rql::to_single(1e300) == std::numeric_limits<float>::infinity() &&
              static_cast<float>(rql::widen_single(3.14159274f)) == 3.14159274f,
          "widen_single: shortest round-trip decimal");
    rql::TypeRegistry reg;
    reg.object<W4Item>("W4Item").member("Name", &W4Item::Name).member("Weight", &W4Item::Weight);
    reg.object<W4Bag>("W4Bag").member("Items", &W4Bag::Items);
    W4Bag bag;
    for (float w : {1.0f, 4.2f}) {
        auto it = std::make_shared<W4Item>();
        it->Name = "i";
        it->Weight = w;
        bag.Items.push_back(it);
    }
    rql::Engine e(reg);
    e.add_object_source("bag", &bag);
    e.mutations().enabled = true;
    auto avg = arun(e, R"J({"rql":"0.1","source":{"path":["bag","Items"]},"stages":[{"op":"avg","expr":{"path":["Weight"]}}]})J");
    auto sum = arun(e, R"J({"rql":"0.1","source":{"path":["bag","Items"]},"stages":[{"op":"sum","expr":{"path":["Weight"]}}]})J");
    auto mul = arun(e, R"J({"rql":"0.1","source":{"path":["bag","Items"]},"stages":[{"op":"select","fields":[{"name":"w","expr":{"op":"*","args":[{"path":["Weight"]},{"lit":1}]}}]}]})J");
    check(avg.ok && avg.value.as_number() == 2.6 && sum.value.as_number() == 5.2 && mul.ok &&
              (*mul.rows)[1][0].as_number() == 4.2,
          "float arithmetic and aggregates use the shortest decimal");
    // A non-boolean condition fails with a boolean test to write instead (spec §1.2).
    auto cond = arun(e, R"J({"rql":"0.1","source":{"path":["bag","Items"]},"stages":[{"op":"where","pred":{"path":["Weight"]}}]})J");
    check(!cond.ok && first_code(cond) == "type-mismatch" && has(cond.diagnostics[0].message, "the condition 'Weight' is the number 1") &&
              has(cond.diagnostics[0].hint, "a condition must be a boolean, e.g. Weight != 0"),
          "non-boolean condition: type-mismatch with a hint");
    auto obj = arun(e, R"J({"rql":"0.1","source":{"path":["bag"]},"stages":[{"op":"select","fields":[{"name":"c","expr":{"call":"if","args":[{"path":["Items"]},{"lit":1},{"lit":0}]}}]}]})J");
    check(!obj.ok && first_code(obj) == "type-mismatch" && has(obj.diagnostics[0].hint, "len(Items) > 0"), "non-boolean if() condition fails");
    // The target limit outside 1 ... ceiling is refused; the host names its argument.
    e.mutations().limit_argument = "max_targets";
    for (int n : {0, 1001}) {
        rql::Budgets b;
        b.max_mutation_targets = n;
        rql::QueryOptions o;
        o.allow_mutation = true;
        auto r = e.execute_json(R"J({"rql":"0.1","source":{"path":["bag","Items"]},"stages":[{"op":"set","assign":[{"target":{"path":["Name"]},"value":{"lit":"x"}}]}]})J", b, o);
        check(!r.ok && first_code(r) == "invalid-value" &&
                  has(r.diagnostics[0].message, "max_targets must be from 1 to 1000 (the host ceiling), got " + std::to_string(n)) &&
                  bag.Items[0]->Name == "i",
              "target limit " + std::to_string(n) + " refused");
    }
    // permission-denied names the host's mutation tool, never options.allowMutation.
    const std::string set_plan = R"J({"rql":"0.1","source":{"path":["bag","Items"]},"stages":[{"op":"set","assign":[{"target":{"path":["Name"]},"value":{"lit":"x"}}]}]})J";
    auto denied = arun(e, set_plan);
    check(first_code(denied) == "permission-denied" && has(denied.diagnostics[0].message, "run it with the mutation tool") &&
              !has(denied.diagnostics[0].message, "allowMutation"),
          "permission-denied: neutral mutation tool");
    e.mutate_hint = "rql-mutate";
    check(has(arun(e, set_plan).diagnostics[0].message, "run it with rql-mutate"), "permission-denied: host mutation tool");
    // A set on a mixed stream names the types that have the member.
    rql::TypeRegistry greg;
    conformance::register_types(greg);
    auto g = conformance::load_graph(repo_root + "/fixtures/cross-runtime/graph.json");
    rql::Engine ge(greg);
    ge.add_object_source("g", g.get());
    ge.mutations().enabled = true;
    auto mixed = arun(ge, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"recurse","via":[{"path":["Children"]}]},{"op":"set","assign":[{"target":{"path":["Power"]},"value":{"lit":1}}]}]})J", true);
    check(!mixed.ok && first_code(mixed) == "unknown-member" && has(mixed.diagnostics[0].message, "(6 of 8 targets)") &&
              has(mixed.diagnostics[0].hint, "only Special has 'Power'") && has(mixed.diagnostics[0].hint, "where $ is Special"),
          "mixed-stream set names the types that have the member");
    // The engine lists its adapters in registration order.
    {
        rql::TypeAdapter ad;
        ad.name = "W4Adapter";
        ad.target = reg.descriptor<W4Item>();
        check(e.adapters().empty(), "adapters(): empty");
        e.add_adapter(std::move(ad));
        check(e.adapters().size() == 1 && e.adapters()[0]->name == "W4Adapter", "adapters(): registered adapter listed");
    }
    // Lists compare by value; group-by merges equal list keys.
    auto lists = arun(ge, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"group-by","expr":{"list":[{"lit":1},{"lit":2}]}}]})J");
    check(lists.ok && lists.rows && lists.rows->size() == 1, "group-by a literal list: one group");
}

/// A list that contains itself (encoder cycle guard).
class SelfList final : public rql::IListAdapter, public std::enable_shared_from_this<SelfList> {
public:
    size_t size() const override { return 2; }
    rql::Value at(size_t i) const override {
        if (i == 0) return rql::Value::number(1);
        return rql::Value::list(std::const_pointer_cast<SelfList>(shared_from_this()));
    }
    const rql::TypeDescriptor* type() const override { return nullptr; }
};

/// ADR-0008 bulk create (range, list sources, source-qualified call), the encoder cycle guard, adapter members as known
/// names and the shared language rules. The fixtures cover the semantics; these check messages and host-facing text.
void bulk_tests() {
    auto has = [](const std::string& s, const std::string& part) { return s.find(part) != std::string::npos; };
    rql::TypeRegistry greg;
    conformance::register_types(greg);
    auto g = conformance::load_graph(repo_root + "/fixtures/cross-runtime/graph.json");
    rql::Engine ge(greg);
    ge.add_object_source("g", g.get());
    ge.mutations().enabled = true;
    conformance::register_test_adapters(ge, greg);
    const std::string ell = "\xE2\x80\xA6";
    // Created objects: the receipt carries their $ref.
    auto spawn = arun(ge, R"J({"rql":"0.1","source":{"call":"range","args":[{"lit":0},{"lit":2}]},"stages":[{"op":"call","method":"spawn","on":{"call":"ref","args":[{"lit":"Node#8"}]},"args":[{"op":"+","args":[{"lit":"k"},{"path":[]}]}]}]})J", true);
    check(spawn.ok && spawn.rows && spawn.rows->size() == 2 && (*spawn.rows)[1][0].as_string() == "Node#802", "qualified call: receipt has the created object's $ref");
    // Limit counts invocations ("items reached"), validators name the item.
    auto limit = arun(ge, R"J({"rql":"0.1","source":{"call":"range","args":[{"lit":0},{"lit":25}]},"stages":[{"op":"call","method":"greet","on":{"call":"ref","args":[{"lit":"Node#8"}]},"args":[{"lit":"x"}]}]})J", true);
    check(first_code(limit) == "mutation-limit" && has(limit.diagnostics[0].message, "25 items reached 'call'"), "qualified call: the limit counts items");
    auto bad = arun(ge, R"J({"rql":"0.1","source":{"list":[{"lit":"a"},{"lit":""}]},"stages":[{"op":"call","method":"spawn","on":{"call":"ref","args":[{"lit":"Node#8"}]},"args":[{"path":[]}]}]})J", true);
    check(first_code(bad) == "invalid-value" && has(bad.diagnostics[0].message, "for item 1"), "qualified call: a rejection names the item");
    // An unqualified call on numbers hints the qualified form; a member path is not a source.
    auto numbers = arun(ge, R"J({"rql":"0.1","source":{"call":"range","args":[{"lit":0},{"lit":2}]},"stages":[{"op":"call","method":"bump","args":[]}]})J", true);
    check(first_code(numbers) == "invalid-plan" && has(numbers.diagnostics[0].hint, "call <source>.bump(" + ell + " $ " + ell + ")"), "unqualified call on numbers: qualified hint");
    auto member = arun(ge, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"call","method":"bump","on":{"path":["Children"]},"args":[]}]})J", true);
    check(first_code(member) == "unknown-source" && has(member.diagnostics[0].hint, "get Children | call bump(" + ell + ")"), "qualified call on a member: get hint");
    auto list = arun(ge, R"J({"rql":"0.1","source":{"call":"range","args":[{"lit":0},{"lit":2}]},"stages":[{"op":"call","method":"bump","on":{"path":["g","Nodes"]},"args":[]}]})J", true);
    check(first_code(list) == "invalid-plan" && has(list.diagnostics[0].message, "is a list of 3 item(s)"), "qualified call on a list: invalid-plan");
    // range() as a source and a function; the $path of its items.
    auto paths = arun(ge, R"J({"rql":"0.1","source":{"call":"range","args":[{"lit":10},{"lit":4},{"lit":-2}]},"stages":[{"op":"select","fields":[{"name":"p","expr":{"path":[{"meta":"path"}]}},{"name":"v","expr":{"path":[]}}]}]})J");
    check(paths.ok && paths.rows && paths.rows->size() == 3 && (*paths.rows)[2][0].as_string() == "range(" + ell + ")[2]" && (*paths.rows)[2][1].as_number() == 6, "range: numbers and $path");
    // A self-containing list encodes the repetition as "…".
    {
        rql::TypeRegistry reg;
        rql::Engine e(reg);
        auto self = std::make_shared<SelfList>();
        e.add_source("ls", [self]() { return rql::Value::list(self); });
        auto r = arun(e, R"J({"rql":"0.1","source":{"path":["ls"]},"stages":[]})J");
        check(r.ok && r.items && r.items->size() == 2 && (*r.items)[1].is_array() && (*r.items)[1].size() == 2 && (*r.items)[1].at(1).as_string() == ell,
              "self-containing list encodes");
    }
    // Members of adapters' target types are known names without a host hook.
    {
        rql::TypeRegistry reg;
        reg.object<W4Item>("W4Item").member("Name", &W4Item::Name);
        rql::Engine e(reg);
        e.add_source("objs", []() { return rql::Value::list_of({}); });
        e.mutations().enabled = true;
        const std::string plan = R"J({"rql":"0.1","source":{"path":["objs"]},"stages":[{"op":"where","pred":{"op":">","args":[{"path":["glow"]},{"lit":0}]}},{"op":"set","assign":[{"target":{"path":["Name"]},"value":{"lit":"x"}}]}]})J";
        check(has_diag(arun(e, plan, true), "unknown-member", rql::Severity::Warning), "glow unknown before its adapter");
        rql::TypeAdapter ad;
        ad.name = "Glow";
        ad.target = reg.descriptor<W4Item>();
        ad.configure_members = [](rql::MemberTable& t) { t.computed<W4Item>("glow", [](W4Item&) { return 1.0; }, "a glow"); };
        e.add_adapter(std::move(ad));
        check(!has_diag(arun(e, plan, true), "unknown-member", rql::Severity::Warning), "adapter members are known names");
    }
    // The group-row hint names the rows' own key columns and never offers a missing key column to aggregate.
    {
        auto grp = arun(ge, R"J({"rql":"0.1","source":{"path":["g","Nodes"]},"stages":[{"op":"group-by","fields":[{"name":"mat","expr":{"path":["Kind"]}}]},{"op":"select","fields":[{"name":"key","expr":{"path":["key"]}}]}]})J");
        bool ok = false;
        for (auto& d : grp.diagnostics)
            if (d.code == "unknown-member") ok = has(d.hint, "select mat, count, avg = items.avg(" + ell + ")") && !has(d.hint, "items.avg(key)");
        check(ok, "group-row hint names the key columns");
    }
    // The language rules are the shared fixture text.
    {
        std::ifstream in(repo_root + "/fixtures/guide/language-rules.txt", std::ios::binary);
        std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        std::string lf;
        for (char ch : text)
            if (ch != '\r') lf += ch;
        check(!lf.empty() && lf == rql::language_rules(), "language rules match fixtures/guide/language-rules.txt");
    }
}

// Wave 5: component-wise struct arithmetic (spec §4.2), rounded struct keys (§5), nested members in hints (§10).
struct FVec3f {
    float X = 0, Y = 0, Z = 0;
};
struct FBodyInstance {
    std::string CollisionProfileName;
    double Damping = 0;
};
struct UCoin {
    std::string Name;
    FVec3f Scale;
    FBodyInstance BodyInstance;
};
struct UCoinWorld {
    std::vector<std::unique_ptr<UCoin>> Coins;
};
struct FAnchorsT {
    FVec3f Minimum, Maximum;
};
struct USlot {
    FAnchorsT Anchors;
};
struct UHolder {
    double Threshold = 3;
    std::vector<std::map<std::string, double>> Entries;
};

void wave5_tests() {
    auto has = [](const std::string& s, const std::string& part) { return s.find(part) != std::string::npos; };
    rql::TypeRegistry reg;
    reg.structure<FVec3f>("FVector3f").member("X", &FVec3f::X).member("Y", &FVec3f::Y).member("Z", &FVec3f::Z);
    reg.structure<FBodyInstance>("FBodyInstance").member("CollisionProfileName", &FBodyInstance::CollisionProfileName).member("Damping", &FBodyInstance::Damping);
    reg.object<UCoin>("UCoin").member("Name", &UCoin::Name).member("Scale", &UCoin::Scale).member("BodyInstance", &UCoin::BodyInstance).no_identity();
    reg.object<UCoinWorld>("UCoinWorld").member("Coins", &UCoinWorld::Coins);
    UCoinWorld world;
    for (int i = 0; i < 2; ++i) {
        world.Coins.push_back(std::make_unique<UCoin>());
        world.Coins.back()->Name = "Coin_" + std::to_string(i);
        world.Coins.back()->Scale = FVec3f{i == 0 ? 1.0f : 0.99999994f, 1, 1};
    }
    const std::string coins = R"J({"rql":"0.1","source":{"path":["w","Coins"]},"stages":[)J";
    const std::string scale2 = R"J({"op":"select","fields":[{"name":"p","expr":{"op":"*","args":[{"path":["Scale"]},{"lit":2}]}}]})J";
    {
        rql::Engine e(reg);
        e.add_object_source("w", &world);
        e.mutations().enabled = true;
        // Distinct / group-by key float structs by their members rounded as tuples print: 1 and 0.99999994 merge.
        auto d = arun(e, coins + R"J({"op":"select","fields":[{"name":"Scale","expr":{"path":["Scale"]}}]},{"op":"distinct"}]})J");
        check(d.ok && d.rows && d.rows->size() == 1, "wave5: distinct merges float structs that print alike");
        auto g = arun(e, coins + R"J({"op":"group-by","expr":{"path":["Scale"]}}]})J");
        check(g.ok && g.rows && g.rows->size() == 1 && (*g.rows)[0][1].as_number() == 2, "wave5: group-by merges float structs that print alike");
        // Without an arithmetic adapter a struct product is a type-mismatch error value.
        auto off = arun(e, coins + scale2 + "]}");
        check(off.ok && off.rows && (*off.rows)[0][0].is_object() && (*off.rows)[0][0].contains("$error"), "wave5: no components → type-mismatch cell");
        // Nested struct members in unknown-member hints.
        auto w = arun(e, coins + R"J({"op":"set","assign":[{"target":{"path":["CollisionProfileName"]},"value":{"lit":"NoCollision"}}]}]})J", true);
        check(!w.ok && first_code(w) == "unknown-member" && has(w.diagnostics[0].hint, "did you mean BodyInstance.CollisionProfileName"),
              "wave5: set hint finds BodyInstance.CollisionProfileName");
        auto rd = arun(e, coins + R"J({"op":"where","pred":{"op":">","args":[{"path":["Damping"]},{"lit":0}]}},{"op":"count"}]})J");
        bool found = false;
        for (auto& diag : rd.diagnostics) found = found || has(diag.hint, "BodyInstance.Damping");
        check(found, "wave5: read hint finds BodyInstance.Damping");
    }
    {
        rql::Engine e(reg);
        e.add_object_source("w", &world);
        e.mutations().enabled = true;
        rql::TypeRegistry* rp = &reg;
        rql::TypeAdapter vec;
        vec.target = reg.descriptor<FVec3f>();
        vec.components = [rp](const rql::Value& v) -> std::optional<std::vector<double>> {
            const FVec3f* p = rql::value_as<FVec3f>(*rp, v);
            if (!p) return std::nullopt;
            return std::vector<double>{rql::widen_single(p->X), rql::widen_single(p->Y), rql::widen_single(p->Z)};
        };
        vec.from_components = [rp](const rql::TypeDescriptor&, const std::vector<double>& c) -> std::optional<rql::Value> {
            if (c.size() != 3) return std::nullopt;
            FVec3f out{rql::to_single(c[0]), rql::to_single(c[1]), rql::to_single(c[2])};
            return rql::Traits<FVec3f>::get(*rp, out);
        };
        e.add_adapter(std::move(vec));
        auto r = arun(e, coins + R"J({"op":"set","assign":[{"target":{"path":["Scale"]},"value":{"op":"*","args":[{"path":["Scale"]},{"lit":1.5}]}}]}]})J", true);
        check(r.ok && world.Coins[0]->Scale.X == 1.5f && world.Coins[0]->Scale.Z == 1.5f, "wave5: set Scale = Scale * 1.5 with components");
        auto mix = arun(e, coins + R"J({"op":"select","fields":[{"name":"p","expr":{"op":"+","args":[{"path":["Scale"]},{"lit":1}]}}]}]})J");
        check(mix.ok && mix.rows && (*mix.rows)[0][0].contains("$error"), "wave5: vector + number → type-mismatch cell");
        auto lst = arun(e, coins + R"J({"op":"select","fields":[{"name":"x","expr":{"path":["X"],"on":{"op":"+","args":[{"path":["Scale"]},{"list":[{"lit":1},{"lit":0},{"lit":0}]}]}}}]}]})J");
        check(lst.ok && lst.rows && (*lst.rows)[0][0].as_number() == 2.5, "wave5: vector + [1, 0, 0]");
    }
    {
        // The scope chain falls back past an absent map key on the element (spec §3).
        rql::TypeRegistry hr;
        hr.object<UHolder>("UHolder").member("Threshold", &UHolder::Threshold).member("Entries", &UHolder::Entries).no_identity();
        UHolder holder;
        holder.Entries.push_back({{"v", 5.0}});
        rql::Engine e(hr);
        e.add_object_source("h", &holder);
        auto r = arun(e, R"J({"rql":"0.1","source":{"path":["h"]},"stages":[{"op":"where","pred":{"call":"any","on":{"path":["Entries"]},"args":[{"op":">","args":[{"path":["v"]},{"path":["Threshold"]}]}]}},{"op":"count"}]})J");
        check(r.ok && r.value.as_number() == 1, "wave5: scope chain falls back past an absent key");
    }
    {
        // A struct of structs written with its current value is unchanged (spec §4.1 member-wise struct equality).
        rql::TypeRegistry sr;
        sr.structure<FVec3f>("FVector3f").member("X", &FVec3f::X).member("Y", &FVec3f::Y).member("Z", &FVec3f::Z);
        sr.structure<FAnchorsT>("FAnchors").member("Minimum", &FAnchorsT::Minimum).member("Maximum", &FAnchorsT::Maximum);
        sr.object<USlot>("USlot").member("Anchors", &USlot::Anchors).no_identity();
        USlot slot;
        slot.Anchors.Maximum.X = 0.5f;
        rql::Engine e(sr);
        e.add_object_source("s", &slot);
        e.mutations().enabled = true;
        auto same = arun(e, R"J({"rql":"0.1","source":{"path":["s"]},"stages":[{"op":"set","assign":[{"target":{"path":["Anchors"]},"value":{"path":["Anchors"]}}]}]})J", true);
        check(same.ok && same.rows && same.rows->empty() && same.unchanged == 1, "wave5: set nested struct to its value → unchanged");
        auto eq = arun(e, R"J({"rql":"0.1","source":{"path":["s"]},"stages":[{"op":"select","fields":[{"name":"e","expr":{"op":"==","args":[{"path":["Anchors"]},{"path":["Anchors"]}]}}]}]})J");
        check(eq.ok && eq.rows && (*eq.rows)[0][0].is_bool() && (*eq.rows)[0][0].as_bool(), "wave5: struct == struct member-wise");
    }
}

// ADR-0010: overload resolution without side effects, engine objects never built from literals, the best-matching
// overload's failure and arguments by name. Mirrors dotnet/Rql.Tests/OverloadTests.cs.
int ov_objects_built = 0;
int ov_placements_built = 0;
struct OvObject {
    OvObject() { ++ov_objects_built; }
    int Id = 0;
    std::string Name;
};
struct OvPlacement {
    OvPlacement() { ++ov_placements_built; }
    double x = 0;
    double y = 0;
};
struct OvEditor {
    std::vector<std::string> made;
};

void overload_tests() {
    auto has = [](const std::string& s, const std::string& part) { return s.find(part) != std::string::npos; };
    rql::TypeRegistry reg;
    reg.object<OvObject>("OvObject").member("Id", &OvObject::Id).member("Name", &OvObject::Name).constructible();
    reg.object<OvPlacement>("OvPlacement").member("x", &OvPlacement::x).member("y", &OvPlacement::y).no_identity().constructible();
    reg.object<OvEditor>("OvEditor").no_identity();
    rql::TypeRegistry* r = &reg;
    const rql::TypeDescriptor* obj = reg.descriptor<OvObject>();
    const rql::TypeDescriptor* place = reg.descriptor<OvPlacement>();
    const auto str = rql::builtin::string_type();
    const auto num = rql::builtin::double_type();
    auto engine = [&](bool engine_objects) {
        auto e = std::make_unique<rql::Engine>(reg);
        if (engine_objects) {
            rql::TypeAdapter alive;
            alive.name = "OvObjectAdapter";
            alive.target = obj;
            alive.is_alive = [](const rql::Value&) -> std::optional<bool> { return true; };
            e->add_adapter(std::move(alive));
        }
        rql::TypeAdapter ed;
        ed.name = "OvEditorAdapter";
        ed.target = reg.descriptor<OvEditor>();
        ed.configure_members = [=](rql::MemberTable& t) {
            t.method<OvEditor>(
                 "Create", {rql::ParamDescriptor{"name", str, false, false, rql::Value()}, rql::ParamDescriptor{"parent", obj, false, false, rql::Value()}}, str,
                 [](OvEditor& e, const std::vector<rql::Value>& a) {
                     e.made.push_back(a[0].as_string());
                     return rql::Value::string("parent");
                 })
                .method<OvEditor>(
                    "Create",
                    {rql::ParamDescriptor{"name", str, false, false, rql::Value()}, rql::ParamDescriptor{"placement", place, false, false, rql::Value()},
                     rql::ParamDescriptor{"primitive", str, false, true, rql::Value::string("Cube")}},
                    str,
                    [](OvEditor& e, const std::vector<rql::Value>& a) {
                        e.made.push_back(a[0].as_string());
                        return rql::Value::string("placement");
                    })
                .method<OvEditor>(
                    "Spawn",
                    {rql::ParamDescriptor{"name", str, false, false, rql::Value()}, rql::ParamDescriptor{"primitive", str, false, true, rql::Value::string("Cube")},
                     rql::ParamDescriptor{"size", num, false, true, rql::Value::number(1)}},
                    str,
                    [](OvEditor& e, const std::vector<rql::Value>& a) {
                        e.made.push_back(a[0].as_string() + ":" + a[1].as_string() + ":" + rql::format_number_text(a[2].as_number()));
                        return rql::Value::string(e.made.back());
                    },
                    "", [](OvEditor&, const std::vector<rql::Value>& a) -> std::string { return a[2].as_number() <= 0 ? "size must be positive" : ""; })
                .method<OvEditor>("Link", {rql::ParamDescriptor{"other", obj, false, false, rql::Value()}}, str,
                                  [r](OvEditor&, const std::vector<rql::Value>& a) { return rql::Value::string(rql::value_as<OvObject>(*r, a[0])->Name); });
        };
        e->add_adapter(std::move(ed));
        e->mutations().enabled = true;
        return e;
    };
    OvEditor editor;
    auto run = [&](rql::Engine& e, const std::string& method, const std::string& args, bool dry) {
        rql::QueryOptions o;
        o.allow_mutation = true;
        o.dry_run = dry;
        return e.execute_json(R"J({"rql":"0.1","source":{"list":[{"lit":1}]},"stages":[{"op":"call","method":")J" + method +
                                  R"J(","on":{"path":["editor"]},"args":[)J" + args + "]}]}",
                              {}, o);
    };
    const std::string placement = R"J({"object":[{"key":"x","value":{"lit":1}},{"key":"y","value":{"lit":2}}]})J";
    const std::string x = R"J({"lit":"X"})J";
    auto e = engine(true);
    e->add_object_source("editor", &editor);
    ov_objects_built = ov_placements_built = 0;
    auto res = run(*e, "Create", x + "," + placement, true);
    check(res.ok && (*res.rows)[0][1].as_string() == "Create(string name, OvPlacement placement, string primitive)" && ov_objects_built == 0 &&
              ov_placements_built == 1 && editor.made.empty(),
          "overloads: the engine-object overload is checked without constructing; only the chosen argument is built");
    for (bool dry : {true, false}) {
        res = run(*e, "Link", R"J({"object":[]})J", dry);
        check(first_code(res) == "coercion-failed" && has(res.diagnostics[0].message, "OvObject is an engine object") && ov_objects_built == 0,
              "overloads: an empty literal never builds an engine object (the only overload)");
    }
    res = run(*e, "Create", x + R"J(,{"object":[{"key":"positon","value":{"lit":1}}]})J", true);
    check(first_code(res) == "coercion-failed" && has(res.diagnostics[0].message, ": argument 'placement': no member 'positon' on OvPlacement"),
          "overloads: a typo inside a placement is reported against the placement overload");
    res = run(*e, "Create", x + R"J(,{"lit":5})J", true);
    check(has(res.diagnostics[0].message, "no overload fits: Create(string name, OvObject parent): argument 'parent': ") &&
              has(res.diagnostics[0].message, "; Create(string name, OvPlacement placement, string primitive): argument 'placement': "),
          "overloads: candidates that came equally close are all listed");
    res = run(*e, "Create", x + "," + x + "," + x + "," + x, true);
    check(has(res.diagnostics[0].message, "expects 2 or 2 to 3 argument(s), got 4"), "overloads: an arity mismatch names the accepted counts");
    // Arguments by name.
    res = run(*e, "Spawn", R"J({"object":[{"key":"size","value":{"lit":2}},{"key":"name","value":{"lit":"A"}}]})J", false);
    check(res.ok && editor.made.size() == 1 && editor.made[0] == "A:Cube:2", "named arguments: bound by name, defaults fill the rest");
    res = run(*e, "Spawn", R"J({"object":[{"key":"name","value":{"lit":"A"}},{"key":"size","value":{"lit":0}}]})J", true);
    check(first_code(res) == "invalid-value" && has(res.diagnostics[0].message, "size must be positive"), "named arguments: validators run on dry runs");
    res = run(*e, "Spawn", R"J({"object":[{"key":"name","value":{"lit":"A"}},{"key":"sise","value":{"lit":2}}]})J", true);
    check(has(res.diagnostics[0].message, "no parameter 'sise' (did you mean 'size'?); parameters: name, primitive, size"), "named arguments: unknown name");
    res = run(*e, "Spawn", R"J({"object":[{"key":"size","value":{"lit":2}}]})J", true);
    check(has(res.diagnostics[0].message, "missing parameter 'name'; parameters: name, primitive, size"), "named arguments: missing required");
    // The host decides.
    auto plain = engine(false);
    plain->add_object_source("editor", &editor);
    const std::string made = R"J({"object":[{"key":"Name","value":{"lit":"made"}}]})J";
    res = run(*plain, "Link", made, false);
    check(res.ok && (*res.rows)[0][2].as_string() == "made", "overloads: without identity or liveness adapters a type is plain data");
    plain->mutations().can_write = [obj](const rql::TypeDescriptor& t, const rql::MemberDescriptor&) { return &t != obj; };
    res = run(*plain, "Link", made, true);
    check(first_code(res) == "permission-denied" && has(res.diagnostics[0].message, "cannot call Link on ") &&
              has(res.diagnostics[0].message, ": argument 'other': member 'Name' of OvObject may not be written"),
          "overloads: a policy denial is the reason when no overload fits");
    plain->mutations().can_write = nullptr;
    plain->mutations().can_construct_from_literal = [obj](const rql::TypeDescriptor& t) -> std::optional<bool> {
        if (&t == obj) return false;
        return std::nullopt;
    };
    check(has(run(*plain, "Link", made, false).diagnostics[0].message, "engine object"), "overloads: the host can refuse a type");
    e->mutations().can_construct_from_literal = [obj](const rql::TypeDescriptor& t) -> std::optional<bool> {
        if (&t == obj) return true;
        return std::nullopt;
    };
    res = run(*e, "Link", made, false);
    check(res.ok && (*res.rows)[0][2].as_string() == "made", "overloads: the host can allow an engine object");
}

// ADR-0011: literal kinds (engine / value / instanced with an outer), the engine-object check before "cannot construct" and
// the host's refusal advice, known names that never evaluate a source, warnings for failing sort keys and sparse group-by /
// distinct keys. Mirrors dotnet/Rql.Tests/Wave6Tests.cs.
int w6_nodes_built = 0;
int w6_subs_built = 0;
struct W6Asset {
    int Id = 0;
    std::string Name;
};
struct W6Box : W6Asset {
    double Size = 1;
};
struct W6Node;
struct W6Sub {
    int Id = 0;
    W6Node* Outer = nullptr;
    W6Sub* Parent = nullptr;
    W6Sub* Inner = nullptr;
    double Threshold = 0;
};
struct W6Place {
    W6Sub* Trigger = nullptr;
    double X = 0;
};
struct W6Node {
    W6Node() { ++w6_nodes_built; }
    int Id = 1;
    std::string Name;
    W6Asset* Mesh = nullptr;
    W6Sub* Trigger = nullptr;
    W6Place Where;
    std::vector<std::string> added;
};
struct W6Thing {
    std::string Name;
    double Mass = 0;
    double Volume = 0;
};
struct W6Bag {
    std::vector<std::shared_ptr<W6Thing>> Items;
};
struct W6Lamp {
    std::string Name;
    std::string type = "Point";
};

// ADR-0012: numeric lists as vectors, vector functions, function / ref() sources and call targets that navigate.
struct VGraph {
    std::vector<std::string> Events;
    std::string AddCustomEvent(std::string name) {
        Events.push_back(name);
        return name;
    }
};
struct VBlueprint {
    std::string Path;
    VGraph EventGraph;
};
struct VActor {
    std::string Name;
    FVec3f Position;
};
struct VWorld {
    std::vector<std::unique_ptr<VActor>> Actors;
};

void vector_tests() {
    auto has = [](const std::string& s, const std::string& part) { return s.find(part) != std::string::npos; };
    rql::TypeRegistry reg;
    reg.structure<FVec3f>("FVector3f").member("X", &FVec3f::X).member("Y", &FVec3f::Y).member("Z", &FVec3f::Z);
    reg.object<VActor>("VActor").member("Name", &VActor::Name).member("Position", &VActor::Position).no_identity();
    reg.object<VWorld>("VWorld").member("Actors", &VWorld::Actors);
    reg.object<VGraph>("VGraph").member("Events", &VGraph::Events).method("AddCustomEvent", &VGraph::AddCustomEvent, {"name"}).no_identity();
    reg.object<VBlueprint>("VBlueprint").member("Path", &VBlueprint::Path).member("EventGraph", &VBlueprint::EventGraph).no_identity();
    VWorld world;
    world.Actors.push_back(std::make_unique<VActor>());
    world.Actors[0]->Name = "a";
    world.Actors[0]->Position = FVec3f{3, 0, 4};
    rql::Engine e(reg);
    e.add_object_source("w", &world);
    e.mutations().enabled = true;
    rql::TypeRegistry* rp = &reg;
    rql::TypeAdapter vec;
    vec.target = reg.descriptor<FVec3f>();
    vec.components = [rp](const rql::Value& v) -> std::optional<std::vector<double>> {
        const FVec3f* p = rql::value_as<FVec3f>(*rp, v);
        if (!p) return std::nullopt;
        return std::vector<double>{rql::widen_single(p->X), rql::widen_single(p->Y), rql::widen_single(p->Z)};
    };
    vec.from_components = [rp](const rql::TypeDescriptor&, const std::vector<double>& c) -> std::optional<rql::Value> {
        if (c.size() != 3) return std::nullopt;
        FVec3f out{rql::to_single(c[0]), rql::to_single(c[1]), rql::to_single(c[2])};
        return rql::Traits<FVec3f>::get(*rp, out);
    };
    e.add_adapter(std::move(vec));
    const std::string actors = R"J({"rql":"0.1","source":{"path":["w","Actors"]},"stages":[)J";
    auto sel = [&](const std::string& expr) {
        return arun(e, actors + R"J({"op":"select","fields":[{"name":"v","expr":)J" + expr + "}]}]}");
    };
    // The reason of a failing expression: set Name = expr fails with it (error cells carry only the code).
    auto why = [&](const std::string& expr) {
        auto res = arun(e, actors + R"J({"op":"set","assign":[{"target":{"path":["Name"]},"value":)J" + expr + "}]}]}", true);
        return res.diagnostics.empty() ? std::string("(ok)") : res.diagnostics[0].message;
    };
    auto r = sel(R"J({"op":"*","args":[{"list":[{"lit":1},{"lit":2},{"lit":0}]},{"lit":3}]})J");
    check(r.ok && r.rows && (*r.rows)[0][0].is_array() && (*r.rows)[0][0].at(1).as_number() == 6, "vectors: [1, 2, 0] * 3");
    check(has(why(R"J({"op":"+","args":[{"list":[{"lit":1},{"lit":2}]},{"lit":1}]})J"), "add a list of 2 numbers"), "vectors: [1, 2] + 1 says why");
    check(has(why(R"J({"op":"*","args":[{"list":[{"lit":1},{"lit":2}]},{"list":[{"lit":3},{"lit":4}]}]})J"), "use dot(a, b)"), "vectors: list * list refused");
    r = sel(R"J({"call":"length","on":{"path":["Position"]},"args":[]})J");
    check(r.ok && (*r.rows)[0][0].as_number() == 5, "vectors: Position.length()");
    r = sel(R"J({"call":"normalize","args":[{"path":["Position"]}]})J");
    check(r.ok && (*r.rows)[0][0].is_object() && (*r.rows)[0][0].find("Z") && (*r.rows)[0][0].find("Z")->as_number() == 0.8, "vectors: normalize keeps the struct type");
    check(has(why(R"J({"call":"dot","args":[{"path":["Position"]},{"list":[{"lit":1},{"lit":2}]}]})J"), "dot() needs vectors of the same width, got 3 and 2 components"),
          "vectors: width mismatch");
    check(has(why(R"J({"call":"length","args":[{"path":["Name"]}]})J"), "length() takes a vector"), "vectors: not a vector");
    check(has(why(R"J({"call":"length","args":[{"path":["Name"]}]})J"), "; for the size of a string, list or map use len(â¦)"),
          "vectors: a string points to len");
    check(!has(why(R"J({"call":"length","args":[{"lit":true}]})J"), "len("), "vectors: a bool gets no len hint");
    r = sel(R"J({"call":"normalize","args":[{"list":[{"lit":0},{"lit":0}]}]})J");
    check(r.ok && (*r.rows)[0][0].is_array() && (*r.rows)[0][0].size() == 2 && (*r.rows)[0][0].at(0).as_number() == 0, "vectors: normalize of a zero list");
    {
        rql::Json caps = e.capabilities();
        auto lists = [&](const char* key, const char* name) {
            const rql::Json* l = caps.find(key);
            if (!l) return false;
            for (size_t i = 0; i < l->size(); ++i)
                if (l->at(i).as_string() == name) return true;
            return false;
        };
        bool all = true;
        for (const char* n : {"length", "magnitude", "normalize", "distance", "dot", "lerp"}) all = all && lists("functions", n) && lists("methods", n);
        check(all, "vectors: the capability manifest lists the vector functions and methods");
    }
    r = arun(e, actors + R"J({"op":"set","assign":[{"target":{"path":["Position"]},"value":{"op":"+","args":[{"path":["Position"]},{"op":"*","args":[{"list":[{"lit":1},{"lit":2},{"lit":0}]},{"lit":2}]}]}}]}]})J", true);
    check(r.ok && world.Actors[0]->Position.X == 5 && world.Actors[0]->Position.Y == 4, "vectors: set Position = Position + [1, 2, 0] * 2");

    // A host function call followed by navigation is a qualified call target, evaluated once (spec §2, §8).
    VBlueprint bp;
    bp.Path = "/Game/BP";
    int resolved = 0;
    e.add_function("unreal.asset", [&bp, &resolved, rp](const std::vector<rql::Value>& a) {
        ++resolved;
        return !a.empty() && a[0].is_string() && a[0].as_string() == bp.Path ? rp->wrap(&bp) : rql::Value::null();
    });
    const std::string target = R"J({"path":["EventGraph"],"on":{"call":"asset","on":{"path":["unreal"]},"args":[{"lit":"/Game/BP"}]}})J";
    r = arun(e, R"J({"rql":"0.1","source":{"list":[{"lit":"Jump"},{"lit":"Land"}]},"stages":[{"op":"call","method":"AddCustomEvent","on":)J" + target +
                    R"J(,"args":[{"path":[]}]}]})J",
             true);
    check(r.ok && bp.EventGraph.Events.size() == 2 && bp.EventGraph.Events[1] == "Land" && resolved == 1,
          "vectors: call unreal.asset(...).EventGraph.AddCustomEvent($) once per item");
    r = arun(e, R"J({"rql":"0.1","source":{"path":["EventGraph","Events",{"index":1}],"on":{"call":"asset","on":{"path":["unreal"]},"args":[{"lit":"/Game/BP"}]}},"stages":[]})J");
    check(r.ok && r.items && (*r.items).size() == 1 && (*r.items)[0].as_string() == "Land", "vectors: function source with navigation");
}

void wave6_tests() {
    auto has = [](const std::string& s, const std::string& part) { return s.find(part) != std::string::npos; };
    rql::TypeRegistry reg;
    reg.object<W6Asset>("W6Asset").member("Id", &W6Asset::Id).member("Name", &W6Asset::Name);
    reg.object<W6Box>("W6Box").base<W6Asset>().member("Size", &W6Box::Size).constructible();
    reg.object<W6Sub>("W6Sub").member("Id", &W6Sub::Id).member("Threshold", &W6Sub::Threshold).member("Inner", &W6Sub::Inner).constructible();
    reg.structure<W6Place>("W6Place").member("Trigger", &W6Place::Trigger).member("X", &W6Place::X);
    reg.object<W6Node>("W6Node")
        .member("Id", &W6Node::Id)
        .member("Name", &W6Node::Name)
        .member("Mesh", &W6Node::Mesh)
        .member("Trigger", &W6Node::Trigger)
        .member("Where", &W6Node::Where)
        .constructible();
    reg.object<W6Thing>("W6Thing").member("Name", &W6Thing::Name).member("Mass", &W6Thing::Mass).member("Volume", &W6Thing::Volume).no_identity();
    reg.object<W6Bag>("W6Bag").member("Items", &W6Bag::Items).no_identity();
    reg.object<W6Lamp>("W6Lamp").member("Name", &W6Lamp::Name).member("type", &W6Lamp::type).no_identity();
    rql::TypeRegistry* r = &reg;
    const rql::TypeDescriptor* node_t = reg.descriptor<W6Node>();
    const rql::TypeDescriptor* asset_t = reg.descriptor<W6Asset>();
    const rql::TypeDescriptor* box_t = reg.descriptor<W6Box>();
    const rql::TypeDescriptor* sub_t = reg.descriptor<W6Sub>();
    auto subs = std::make_shared<std::vector<std::unique_ptr<W6Sub>>>();
    auto alive = [](const rql::TypeDescriptor* t, const char* name) {
        rql::TypeAdapter a;
        a.name = name;
        a.target = t;
        a.is_alive = [](const rql::Value&) -> std::optional<bool> { return true; };
        return a;
    };
    W6Node node;
    node.Name = "Player";
    auto engine = [&](bool kinds) {
        auto e = std::make_unique<rql::Engine>(reg);
        e->add_adapter(alive(node_t, "W6NodeAdapter"));
        rql::TypeAdapter asset = alive(asset_t, "W6AssetAdapter");
        rql::TypeAdapter sub = alive(sub_t, "W6SubAdapter");
        if (kinds) {
            asset.literal_kind = [](const rql::TypeDescriptor&) -> std::optional<rql::LiteralKind> { return rql::LiteralKind::Value; };
            sub.literal_kind = [](const rql::TypeDescriptor&) -> std::optional<rql::LiteralKind> { return rql::LiteralKind::Instanced; };
            sub.create_for_literal = [r, subs](const rql::TypeDescriptor&, const rql::Value& outer) -> std::optional<rql::Value> {
                ++w6_subs_built;
                subs->push_back(std::make_unique<W6Sub>());
                W6Sub* s = subs->back().get();
                s->Outer = rql::value_as<W6Node>(*r, outer);
                s->Parent = rql::value_as<W6Sub>(*r, outer);
                return r->wrap(s);
            };
        }
        e->add_adapter(std::move(asset));
        e->add_adapter(std::move(sub));
        rql::TypeAdapter methods;
        methods.name = "W6NodeMethods";
        methods.target = node_t;
        methods.configure_members = [r, node_t, sub_t](rql::MemberTable& t) {
            t.method<W6Node>("AddTrigger", {rql::ParamDescriptor{"trigger", sub_t, false, false, rql::Value()}}, rql::builtin::string_type(),
                             [r](W6Node& n, const std::vector<rql::Value>& a) {
                                 W6Sub* s = rql::value_as<W6Sub>(*r, a[0]);
                                 n.added.push_back(rql::format_number_text(s->Threshold));
                                 return rql::Value::string(s->Outer ? s->Outer->Name : "-");
                             })
                .method<W6Node>("Attach", {rql::ParamDescriptor{"node", node_t, false, false, rql::Value()}}, rql::builtin::string_type(),
                                [r](W6Node&, const std::vector<rql::Value>& a) { return rql::Value::string(rql::value_as<W6Node>(*r, a[0])->Name); });
        };
        e->add_adapter(std::move(methods));
        e->add_object_source("player", &node);
        e->mutations().enabled = true;
        return e;
    };
    rql::QueryOptions mutate;
    mutate.allow_mutation = true;
    rql::QueryOptions dry = mutate;
    dry.dry_run = true;
    auto set_plan = [](const std::string& member, const std::string& value) {
        return R"J({"rql":"0.1","source":{"path":["player"]},"stages":[{"op":"set","assign":[{"target":{"path":[")J" + member +
               R"J("]},"value":)J" + value + "}]}]}";
    };
    auto call_plan = [](const std::string& method, const std::string& arg) {
        return R"J({"rql":"0.1","source":{"path":["player"]},"stages":[{"op":"call","method":")J" + method + R"J(","args":[)J" + arg + "]}]}";
    };
    auto obj = [](std::initializer_list<std::pair<std::string, std::string>> kv) {
        std::string s = R"J({"object":[)J";
        bool first = true;
        for (auto& p : kv) {
            s += (first ? "" : ",") + std::string(R"J({"key":")J") + p.first + R"J(","value":)J" + p.second + "}";
            first = false;
        }
        return s + "]}";
    };
    auto lit = [](const std::string& json) { return R"J({"lit":)J" + json + "}"; };
    const std::string box = obj({{"$type", lit("\"W6Box\"")}, {"Size", lit("2")}});

    // #1 value-like engine types.
    {
        auto e = engine(true);
        auto res = e->execute_json(set_plan("Mesh", box), {}, mutate);
        check(res.ok && node.Mesh && static_cast<W6Box*>(node.Mesh)->Size == 2, "literal kinds: a value kind builds an identified type from a literal");
        node.Mesh = nullptr;
        auto plain = engine(false);
        res = plain->execute_json(set_plan("Mesh", box), {}, mutate);
        check(first_code(res) == "coercion-failed" && has(res.diagnostics[0].message, "W6Box is an engine object") && !node.Mesh,
              "literal kinds: without a kind an identified type is an engine object");
        plain->mutations().literal_kind_of = [asset_t](const rql::TypeDescriptor& t) -> std::optional<rql::LiteralKind> {
            if (t.derives_from(asset_t)) return rql::LiteralKind::Value;
            return std::nullopt;
        };
        check(plain->execute_json(set_plan("Mesh", box), {}, mutate).ok, "literal kinds: the host's literal-kind hook decides");
        node.Mesh = nullptr;
        e->mutations().can_construct_from_literal = [box_t](const rql::TypeDescriptor& t) -> std::optional<bool> {
            if (&t == box_t) return false;
            return std::nullopt;
        };
        check(has(e->execute_json(set_plan("Mesh", box), {}, mutate).diagnostics[0].message, "engine object"),
              "literal kinds: can_construct_from_literal false makes a value kind an engine object");
        auto allow = engine(false);
        allow->mutations().can_construct_from_literal = [box_t](const rql::TypeDescriptor& t) -> std::optional<bool> {
            if (&t == box_t) return true;
            return std::nullopt;
        };
        check(allow->execute_json(set_plan("Mesh", box), {}, mutate).ok, "literal kinds: can_construct_from_literal true makes an engine type a value");
        node.Mesh = nullptr;
        // A host factory gets the outer (the owner of the written member).
        auto fe = engine(true);
        auto made = std::make_shared<W6Box>();
        const W6Node* seen = nullptr;
        fe->mutations().create_for_literal = [r, made, &seen](const rql::TypeDescriptor&, const rql::Value& outer) -> std::optional<rql::Value> {
            seen = rql::value_as<W6Node>(*r, outer);
            made->Size = 7;
            return r->wrap(made.get());
        };
        res = fe->execute_json(set_plan("Mesh", obj({{"$type", lit("\"W6Box\"")}})), {}, mutate);
        check(res.ok && node.Mesh == made.get() && made->Size == 7 && seen == &node, "literal kinds: the host factory builds values for their outer");
        node.Mesh = nullptr;
    }

    // #21 instanced subobjects.
    {
        auto e = engine(true);
        auto res = e->execute_json(set_plan("Trigger", obj({{"Threshold", lit("0.5")}})), {}, mutate);
        check(res.ok && node.Trigger && node.Trigger->Outer == &node && node.Trigger->Threshold == 0.5,
              "instanced: built for the owner of the written member");
        res = e->execute_json(set_plan("Trigger", obj({{"Threshold", lit("1")}, {"Inner", obj({{"Threshold", lit("2")}})}})), {}, mutate);
        check(res.ok && node.Trigger->Inner && node.Trigger->Inner->Parent == node.Trigger && !node.Trigger->Inner->Outer,
              "instanced: a nested literal is owned by the object the enclosing literal builds");
        res = e->execute_json(set_plan("Where", obj({{"X", lit("1")}, {"Trigger", obj({{"Threshold", lit("3")}})}})), {}, mutate);
        check(res.ok && node.Where.Trigger && node.Where.Trigger->Outer == &node, "instanced: a struct literal passes its own outer on");
        res = e->execute_json(call_plan("AddTrigger", obj({{"Threshold", lit("0.5")}})), {}, mutate);
        check(res.ok && (*res.rows)[0][2].as_string() == "Player", "instanced: a call argument is built for the target");
        res = e->execute_json(R"J({"rql":"0.1","source":{"call":"range","args":[{"lit":0},{"lit":2}]},"stages":[{"op":"call","method":"AddTrigger","on":{"path":["player"]},"args":[)J" +
                                  obj({{"Threshold", R"J({"path":[]})J"}}) + "]}]}",
                              {}, mutate);
        check(res.ok && (*res.rows)[1][2].as_string() == "Player", "instanced: a source-qualified call builds for the receiver");
        node.added.clear();
        w6_nodes_built = 0;
        res = e->execute_json(call_plan("Attach", obj({{"Name", lit("\"x\"")}})), {}, mutate);
        check(first_code(res) == "coercion-failed" && has(res.diagnostics[0].message, "W6Node is an engine object") && w6_nodes_built == 0,
              "instanced: the probe never builds an engine object");
        w6_subs_built = 0;
        res = e->execute_json(call_plan("AddTrigger", obj({{"Threshold", lit("2")}})), {}, dry);
        check(res.ok && w6_subs_built == 1 && node.added.empty(), "instanced: a dry run builds the planned value (planning) and writes nothing");
        // The host replaces the refusal advice.
        e->mutations().literal_refusal_hint = [](const rql::TypeDescriptor&) { return std::string("use scene.find(\xE2\x80\xA6) or an asset path"); };
        res = e->execute_json(call_plan("Attach", obj({{"Name", lit("\"x\"")}})), {}, mutate);
        check(has(res.diagnostics[0].message, "cannot be built from an object literal; use scene.find(\xE2\x80\xA6) or an asset path"),
              "literal kinds: the host replaces the refusal advice");
    }
    // The engine-object refusal comes before "cannot construct": W6Asset is not constructible.
    {
        auto plain = engine(false);
        auto res = plain->execute_json(set_plan("Mesh", obj({{"Name", lit("\"x\"")}})), {}, mutate);
        check(has(res.diagnostics[0].message, "W6Asset is an engine object and cannot be built from an object literal; pass an existing one"),
              "literal kinds: an engine object that cannot be constructed gets the engine-object refusal");
        auto e = engine(true);
        res = e->execute_json(set_plan("Mesh", obj({{"Name", lit("\"x\"")}})), {}, mutate);
        check(has(res.diagnostics[0].message, "cannot construct W6Asset"), "literal kinds: a value kind without a constructor says so");
    }

    // #15 known names never evaluate a source the query does not name.
    {
        int walks = 0, own = 0;
        W6Thing a;
        a.Name = "a";
        rql::Engine e(reg);
        e.add_source("items", [r, &a, &own]() {
            ++own;
            return rql::Value::list_of({r->wrap(&a)});
        });
        e.add_source("groups", [&walks]() {
            ++walks;
            return rql::Value::list_of({});
        });
        e.mutations().enabled = true;
        const std::string src = R"J({"rql":"0.1","source":{"path":["items"]},"stages":[)J";
        e.execute_json(src + R"J({"op":"where","pred":{"op":"==","args":[{"path":["Labl"]},{"lit":"x"}]}}]})J");
        e.execute_json(src + R"J({"op":"where","pred":{"op":"is","args":[{"path":[]},{"lit":"W6Node"}]}},{"op":"count"}]})J");
        e.execute_json(src + R"J({"op":"where","pred":{"op":"==","args":[{"path":["Labl"]},{"lit":"x"}]}},{"op":"set","assign":[{"target":{"path":["Name"]},"value":{"lit":"y"}}]}]})J",
                       {}, mutate);
        check(walks == 0, "known names: warnings never evaluate a source the query does not name");
        // A typed source (its type tells the members) read only by the known-names pass.
        rql::Engine e2(reg);
        own = 0;
        W6Bag bag;
        bag.Items.push_back(std::make_shared<W6Thing>());
        e2.add_source("bag", [r, &bag, &own]() {
            ++own;
            return r->wrap(&bag);
        });
        auto res = e2.execute_json(R"J({"rql":"0.1","source":{"path":["bag","Items"]},"stages":[{"op":"sort","keys":[{"expr":{"path":["Name"]}}]},{"op":"first","n":0},{"op":"count"}]})J");
        bool warned = std::any_of(res.diagnostics.begin(), res.diagnostics.end(), [](const rql::Diagnostic& d) { return d.code == "unknown-member"; });
        check(res.ok && !warned && own == 1, "known names: the query's own source is read once when its stream never pulled it");
    }
    // Known names: the own untyped source is read once per query; a declared type is known without its getter.
    {
        int own = 0, later = 0;
        W6Thing a;
        rql::Engine e(reg);
        e.add_source("items", [r, &a, &own]() {
            ++own;
            return rql::Value::list_of({r->wrap(&a)});
        });
        W6Bag bag;
        e.add_source("later", [r, &bag, &later]() {
            ++later;
            return r->wrap(&bag);
        }, "", reg.descriptor<W6Bag>());
        e.execute_json(R"J({"rql":"0.1","source":{"path":["items"]},"stages":[{"op":"where","pred":{"op":"==","args":[{"path":["Labl"]},{"lit":"x"}]}}]})J");
        check(own == 1, "known names: the query's own untyped source is read at most once per query");
        auto res = e.execute_json(
            R"J({"rql":"0.1","source":{"path":["items"]},"stages":[{"op":"where","pred":{"op":"and","args":[{"lit":false},{"op":">","args":[{"path":["Mass"]},{"lit":1}]}]}},{"op":"count"}]})J");
        bool warned = std::any_of(res.diagnostics.begin(), res.diagnostics.end(), [](const rql::Diagnostic& d) { return d.code == "unknown-member"; });
        check(res.ok && !warned && later == 0, "known names: a declared source type is known without calling its getter");
    }
    // Factories: a throwing factory fails the build; a null answer falls back to create().
    {
        auto e = engine(true);
        e->mutations().create_for_literal = [](const rql::TypeDescriptor&, const rql::Value&) -> std::optional<rql::Value> {
            throw std::runtime_error("no meshes today");
        };
        auto res = e->execute_json(set_plan("Mesh", box), {}, mutate);
        check(first_code(res) == "coercion-failed" && has(res.diagnostics[0].message, "building W6Box failed: no meshes today") && !node.Mesh,
              "factories: a throwing factory fails the build");
        e->mutations().create_for_literal = [](const rql::TypeDescriptor&, const rql::Value&) -> std::optional<rql::Value> { return rql::Value::null(); };
        res = e->execute_json(set_plan("Mesh", box), {}, mutate);
        check(res.ok && node.Mesh && static_cast<W6Box*>(node.Mesh)->Size == 2, "factories: a null answer falls back to create()");
        node.Mesh = nullptr;
    }

    // #13 sort keys that fail, #12 sparse keys.
    {
        std::vector<std::unique_ptr<W6Thing>> things;
        for (int i = 0; i < 2; ++i) {
            things.push_back(std::make_unique<W6Thing>());
            things.back()->Name = i == 0 ? "a" : "b";
            things.back()->Mass = i + 1;
            things.back()->Volume = i;
        }
        rql::Engine e(reg);
        e.add_source("things", [r, &things]() {
            std::vector<rql::Value> v;
            for (auto& t : things) v.push_back(r->wrap(t.get()));
            return rql::Value::list_of(std::move(v));
        });
        auto res = e.execute_json(
            R"J({"rql":"0.1","source":{"path":["things"]},"stages":[{"op":"sort","keys":[{"expr":{"op":"/","args":[{"path":["Mass"]},{"path":["Volume"]}]},"desc":true}]},{"op":"select","fields":[{"name":"Name","expr":{"path":["Name"]}}]}]})J");
        check(res.ok && res.diagnostics.size() == 1 && res.diagnostics[0].code == "div-by-zero" &&
                  has(res.diagnostics[0].message, "sort key 1 failed for 1 of 2 item(s)") && has(res.diagnostics[0].message, "they sort last") &&
                  (*res.rows)[0][0].as_string() == "b",
              "sort: a failing key warns with the error's code");
        res = e.execute_json(R"J({"rql":"0.1","source":{"path":["things"]},"stages":[{"op":"sort","keys":[{"expr":{"path":["Mass"]}}]}]})J");
        check(res.ok && res.diagnostics.empty(), "sort: a key that never fails is quiet");

        std::vector<std::unique_ptr<W6Thing>> many;
        for (int i = 0; i < 9; ++i) many.push_back(std::make_unique<W6Thing>());
        W6Lamp lamp;
        rql::Engine g(reg);
        g.add_source("items", [r, &many, &lamp]() {
            std::vector<rql::Value> v;
            for (auto& t : many) v.push_back(r->wrap(t.get()));
            v.push_back(r->wrap(&lamp));
            return rql::Value::list_of(std::move(v));
        });
        res = g.execute_json(R"J({"rql":"0.1","source":{"path":["items"]},"stages":[{"op":"group-by","expr":{"path":["type"]}}]})J");
        check(res.ok && res.diagnostics.size() == 1 && res.diagnostics[0].code == "unknown-member" &&
                  res.diagnostics[0].message == "member 'type' exists on 1 of 10 item(s); the other 9 group as null" &&
                  res.diagnostics[0].hint == "did you mean $type (the meta member)?",
              "group-by: a sparse member warns and offers the meta member");
        res = g.execute_json(R"J({"rql":"0.1","source":{"path":["items"]},"stages":[{"op":"distinct","expr":{"path":["type"]}},{"op":"count"}]})J");
        check(res.ok && res.diagnostics.size() == 1 && res.diagnostics[0].message == "member 'type' exists on 1 of 10 item(s); the other 9 are kept as one",
              "distinct: a sparse member warns");
        res = g.execute_json(R"J({"rql":"0.1","source":{"path":["items"]},"stages":[{"op":"group-by","expr":{"path":["Volume"]}}]})J");
        check(res.ok && res.diagnostics.empty(), "group-by: a member on most items is quiet");
    }
}

// ADR-0013: misspelt parameter names rank first, the host's creation call in the rules, receipts never suggest paging.
struct HnOptions {
    std::string name;
};
struct HnGodot {
    std::vector<std::string> made;
};
struct HnThing {
    int Id = 0;
    std::string Name;
};

void hint_tests() {
    auto has = [](const std::string& s, const std::string& part) { return s.find(part) != std::string::npos; };
    {
        rql::TypeRegistry reg;
        reg.object<HnOptions>("NodeOptions").member("name", &HnOptions::name).no_identity().constructible();
        reg.object<HnGodot>("Godot").no_identity();
        rql::TypeRegistry* r = &reg;
        const auto str = rql::builtin::string_type();
        const rql::TypeDescriptor* opts = reg.descriptor<HnOptions>();
        rql::Engine e(reg);
        rql::TypeAdapter g;
        g.name = "GodotAdapter";
        g.target = reg.descriptor<HnGodot>();
        g.configure_members = [=](rql::MemberTable& t) {
            t.method<HnGodot>("CreateNode", {rql::ParamDescriptor{"options", opts, false, false, rql::Value()}}, str,
                              [r](HnGodot& gd, const std::vector<rql::Value>& a) {
                                  gd.made.push_back(rql::value_as<HnOptions>(*r, a[0])->name);
                                  return rql::Value::string(gd.made.back());
                              })
                .method<HnGodot>("CreateNode",
                                 {rql::ParamDescriptor{"className", str, false, false, rql::Value()},
                                  rql::ParamDescriptor{"name", str, false, true, rql::Value::string("")},
                                  rql::ParamDescriptor{"position", str, false, true, rql::Value::string("")}},
                                 str, [](HnGodot& gd, const std::vector<rql::Value>& a) {
                                     gd.made.push_back(a[0].as_string());
                                     return rql::Value::string(gd.made.back());
                                 });
        };
        e.add_adapter(std::move(g));
        e.mutations().enabled = true;
        HnGodot godot;
        e.add_source("godot", [r, &godot]() { return r->wrap(&godot); });
        rql::QueryOptions o;
        o.allow_mutation = true;
        auto call = [&](const std::string& key) {
            return e.execute_json(R"J({"rql":"0.1","source":{"list":[{"lit":1}]},"stages":[{"op":"call","method":"CreateNode","on":{"path":["godot"]},"args":[{"object":[{"key":")J" +
                                      key + R"J(","value":{"lit":"MeshInstance3D"}}]}]}]})J",
                                  {}, o);
        };
        rql::QueryResult res = call("klass");
        check(!res.ok && res.diagnostics[0].code == "coercion-failed" && has(res.diagnostics[0].message, "no parameter 'klass' (did you mean 'className'?)"),
              "hints: a misspelt parameter name beats the options overload: " + (res.diagnostics.empty() ? std::string() : res.diagnostics[0].message));
        res = call("zzzzzz");
        check(!res.ok && !has(res.diagnostics[0].message, "did you mean"), "hints: a key no parameter resembles gets no suggestion");
        check(godot.made.empty(), "hints: nothing created by failing calls");
    }
    {
        const std::string text = rql::language_rules_for("editor.CreateGameObject");
        check(!has(text, "editor.CreateObject") && !has(text, "stands for") && has(text, "call editor.CreateGameObject(\"Cube\"") &&
                  has(text, "call editor.CreateGameObject($[0], $[1])"),
              "hints: the rules take the host creation call");
        check(rql::language_rules_for("") == std::string(rql::language_rules()), "hints: no call keeps the shared rules");
    }
    {
        rql::TypeRegistry reg;
        reg.object<HnThing>("Thing").member("Id", &HnThing::Id).member("Name", &HnThing::Name);
        rql::TypeRegistry* r = &reg;
        std::vector<std::unique_ptr<HnThing>> things;
        for (int i = 1; i <= 200; ++i) {
            things.push_back(std::make_unique<HnThing>());
            things.back()->Id = i;
            things.back()->Name = "t" + std::to_string(i);
        }
        rql::Engine e(reg);
        e.mutations().enabled = true;
        e.mutations().max_targets = 500;
        e.add_source("things", [r, &things]() {
            std::vector<rql::Value> l;
            for (auto& t : things) l.push_back(r->wrap(t.get()));
            return rql::Value::list_of(std::move(l));
        });
        rql::Budgets b;
        b.max_output_bytes = 2000;
        auto set = [&](const std::string& suffix, bool dry) {
            rql::QueryOptions o;
            o.allow_mutation = true;
            o.dry_run = dry;
            return e.execute_json(R"J({"rql":"0.1","source":{"path":["things"]},"stages":[{"op":"set","assign":[{"target":{"path":["Name"]},"value":{"op":"+","args":[{"path":["Name"]},{"lit":")J" +
                                      suffix + R"J("}]}}]}]})J",
                                  b, o);
        };
        rql::QueryResult res = set("-a-fairly-long-suffix-to-fill-the-receipt", false);
        const rql::Diagnostic* w = nullptr;
        for (auto& d : res.diagnostics)
            if (d.code == "output-truncated") w = &d;
        check(res.ok && res.truncated && w && has(w->hint, "the receipt is shortened, not the change: all 200 changes were made") && !has(w->hint, "page"),
              "hints: a shortened receipt says every change was made");
        size_t changed = 0;
        for (auto& t : things)
            if (has(t->Name, "suffix-to-fill-the-receipt")) ++changed;
        check(changed == 200, "hints: every target changed although the receipt is shortened");
        res = set("-b-fairly-long-suffix-to-fill-the-receipt", true);
        w = nullptr;
        for (auto& d : res.diagnostics)
            if (d.code == "output-truncated") w = &d;
        check(w && has(w->hint, "planned changes were checked (dry run)"), "hints: a shortened dry-run receipt says the changes were checked");
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
        wave3_tests();
        wave3b_tests();
        wave4_tests();
        bulk_tests();
        wave5_tests();
        overload_tests();
        wave6_tests();
        vector_tests();
        hint_tests();
    } catch (const std::exception& ex) {
        std::cout << "FATAL " << ex.what() << "\n";
        return 2;
    }
    std::cout << passed << " passed, " << failed << " failed, 0 skipped\n";
    return failed == 0 ? 0 : 1;
}
