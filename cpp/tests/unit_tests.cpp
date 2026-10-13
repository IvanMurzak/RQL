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

    // timeout budget
    rql::Budgets b;
    b.max_visited = 1;
    r = e.execute_json(R"({"rql":"0.1","source":{"path":["h","Counters"]},"stages":[{"op":"count"}]})", b);
    check(!r.ok && first_code(r) == "budget-exceeded", "maxVisited budget");
}

}  // namespace

int main() {
    try {
        json_tests();
        codec_tests();
        engine_tests();
    } catch (const std::exception& ex) {
        std::cout << "FATAL " << ex.what() << "\n";
        return 2;
    }
    std::cout << passed << " passed, " << failed << " failed, 0 skipped\n";
    return failed == 0 ? 0 : 1;
}
