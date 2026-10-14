// Unit tests: JSON edge cases, number formatting, and engine features the golden vectors do not
// cover (call stage, extensions, session handles, liveness, unordered maps, shared_ptr, rollback).
#include <cmath>
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
    check(!r.ok && first_code(r) == "getter-threw", "call: throwing method");
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
    check(res.ok && g->Nodes[1]->Name == "root b2" && (*res.rows)[0][2].as_string() == "ROOT-B" && (*res.rows)[0][3].as_string() == "Root B2",
          "adapters: virtual setter");
    res = arun(e, R"J({"rql":"0.1","source":{"call":"ref","args":[{"lit":"Node#5"}]},"stages":[{"op":"set","assign":[{"target":{"path":["title"]},"value":{"lit":""}}]}]})J", true);
    check(!res.ok && first_code(res) == "coercion-failed" && g->Nodes[1]->Name == "root b2", "adapters: validation rejects before any write");
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

}  // namespace

int main(int argc, char** argv) {
    if (argc > 1) repo_root = argv[1];
    try {
        json_tests();
        codec_tests();
        engine_tests();
        adapter_tests();
    } catch (const std::exception& ex) {
        std::cout << "FATAL " << ex.what() << "\n";
        return 2;
    }
    std::cout << passed << " passed, " << failed << " failed, 0 skipped\n";
    return failed == 0 ? 0 : 1;
}
