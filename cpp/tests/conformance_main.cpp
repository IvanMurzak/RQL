// Golden-vector conformance runner for the C++ runtime (fixtures/). Mirrors
// dotnet/Rql.Conformance/ConformanceSuite.cs "check", minus everything that needs the text parser
// (the C++ runtime executes QueryPlans; RQL text is parsed by the .NET/TS sidecar).
#include <cstdio>
#include <iostream>
#include <string>

#include "conformance_graph.hpp"

using rql::Json;

namespace {

int passed = 0, failed = 0, skipped = 0;
int skipped_syntax_text = 0, skipped_negative_syntax = 0, skipped_exec = 0;
bool verbose = false;

void check(bool ok, const std::string& what) {
    if (ok) {
        ++passed;
        return;
    }
    ++failed;
    std::cout << "FAIL " << what << "\n";
}

std::string F(const std::string& root, const std::string& rel) { return root + "/fixtures/" + rel; }

Json load(const std::string& path) { return Json::parse(conformance::read_file(path)); }

/// Conformance view of a result: no stats/rql, diagnostics reduced to severity+code.
Json normalize(const rql::QueryResult& r) {
    Json o = r.to_json(false);
    o.erase("rql");
    if (Json* diags = o.find("diagnostics")) {
        Json reduced = Json::array();
        for (auto& d : diags->items()) {
            Json x = Json::object();
            x.set("severity", *d.find("severity"));
            x.set("code", *d.find("code"));
            reduced.push(std::move(x));
        }
        *diags = std::move(reduced);
    }
    return o;
}

void syntax_codec(const std::string& root) {
    Json doc = load(F(root, "syntax/cases.json"));
    for (auto& c : doc.find("cases")->items()) {
        const std::string& text = c.find("rql")->as_string();
        ++skipped_syntax_text;  // text → plan comparison needs the parser
        const Json* stored = c.find("plan");
        if (!stored) continue;
        try {
            Json round = rql::encode_plan(rql::decode_plan(*stored));
            check(rql::json_equal(round, *stored), "codec: round-trip mismatch for '" + text + "'\n  got:  " + round.dump() +
                                                       "\n  want: " + stored->dump());
        } catch (const rql::RqlException& ex) {
            check(false, "codec: '" + text + "' stored plan rejected: " + ex.code() + " " + ex.what());
        }
    }
}

void negative_syntax(const std::string& root) {
    Json doc = load(F(root, "negative/syntax.json"));
    skipped_negative_syntax += static_cast<int>(doc.find("cases")->size());
}

void negative_plans(const std::string& root) {
    Json doc = load(F(root, "negative/plans.json"));
    for (auto& c : doc.find("cases")->items()) {
        const Json& plan = *c.find("plan");
        const Json* wantJ = c.find("code");
        std::string want = wantJ && wantJ->is_string() ? wantJ->as_string() : "";
        std::string code;
        try {
            // Through text, like the .NET runner (exercises the JSON parser as well).
            rql::decode_plan(std::string_view(plan.dump()));
        } catch (const rql::RqlException& ex) {
            code = ex.code();
        }
        check(code == want, "negative plan: " + plan.dump() + " -> " + (code.empty() ? "accepted" : code) + ", want " +
                                (want.empty() ? "accepted" : want));
    }
}

void step(const Json& c, const std::string& name, const char* plan_key, const char* expect_key, const std::string& text,
          rql::Engine& engine, const rql::Budgets& budgets, const rql::QueryOptions& options) {
    const Json* plan = c.find(plan_key);
    if (!plan || !plan->is_object()) {
        ++skipped_exec;
        if (verbose) std::cout << "SKIP (needs parser) execution " << name << ": '" << text << "'\n";
        return;
    }
    rql::QueryPlan p;
    try {
        p = rql::decode_plan(*plan);
    } catch (const rql::RqlException& ex) {
        check(false, "execution " + name + ": stored plan rejected: " + ex.what());
        return;
    }
    rql::QueryResult result = engine.execute(p, budgets, options);
    Json got = normalize(result);
    const Json* want = c.find(expect_key);
    check(want && rql::json_equal(got, *want), "execution " + name + ": '" + text + "'\n  got:  " + got.dump() +
                                                   "\n  want: " + (want ? want->dump() : "null") +
                                                   (verbose ? "\n  full: " + result.dump(false) : ""));
}

void cases(const std::string& root, const std::string& file, bool adapters) {
    Json doc = load(F(root, file));
    std::string graph_path = F(root, "cross-runtime/graph.json");
    rql::TypeRegistry reg;
    conformance::register_types(reg);
    for (auto& c : doc.find("cases")->items()) {
        std::string name = (adapters ? "adapters/" : "") + c.find("name")->as_string();
        auto graph = conformance::load_graph(graph_path);  // fresh graph per case
        rql::Engine engine(reg);                            // fresh engine per case
        engine.add_object_source("g", graph.get());
        engine.mutations().enabled = true;
        if (adapters) conformance::register_test_adapters(engine, reg);
        rql::Budgets budgets;
        rql::QueryOptions options;
        try {
            if (const Json* b = c.find("budgets")) budgets = rql::Budgets::from_json(*b);
            if (const Json* o = c.find("options")) options = rql::QueryOptions::from_json(*o);
        } catch (const rql::RqlException& ex) {
            check(false, "execution " + name + ": bad budgets/options: " + ex.what());
            continue;
        }
        step(c, name, "plan", "expect", c.find("rql")->as_string(), engine, budgets, options);
        if (const Json* then = c.find("then"))
            step(c, name + " (then)", "thenPlan", "thenExpect", then->as_string(), engine, rql::Budgets(), rql::QueryOptions());
    }
}

}  // namespace

int main(int argc, char** argv) {
    std::string root = "..";
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "-v" || a == "--verbose") verbose = true;
        else root = a;
    }
    try {
        syntax_codec(root);
        negative_syntax(root);
        negative_plans(root);
        cases(root, "execution/cases.json", false);
        cases(root, "adapters/cases.json", true);  // spec §13 type adapters
    } catch (const std::exception& ex) {
        std::cout << "FATAL " << ex.what() << "\n";
        return 2;
    }
    skipped = skipped_exec + skipped_negative_syntax;
    std::cout << "skipped (needs parser): " << skipped_exec << " execution step(s) without a stored plan, " << skipped_negative_syntax
              << " negative syntax case(s); " << skipped_syntax_text << " syntax text->plan checks not applicable (codec round-trip run instead)\n";
    std::cout << passed << " passed, " << failed << " failed, " << skipped << " skipped\n";
    return failed == 0 ? 0 : 1;
}
