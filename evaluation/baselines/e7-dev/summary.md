### e7-thinking-low

model `deepseek-flash`, reasoning `low`, prompt `v2`, format `table`, toolset `rql`, schemaInPrompt `False`, maxSteps 12, maxRows 50

| Metric | Value |
|---|---:|
| Tasks (n) | 186 |
| Exact success | 185/186 = 99.5% (95% CI 97.0%–99.9%) |
| Total cost (provider-reported tokens × price snapshot) | $0.0981 |
| Cost / task | $0.00053 |
| Cost / successful task | $0.00053 |
| Successful tasks per $ | 1,885 |
| Successful tasks per wall-minute (sequential-equivalent) | 12.12 |
| Mean prompt / completion tokens per task | 9,612 / 433 (reasoning 172) |
| Prompt cache-hit ratio | 83.1% |
| Mean model turns / tool calls / tool errors | 4.18 / 3.64 / 0.08 |
| Protocol errors (turns without a tool call) | 8 |
| Mean tool-result chars per task | 4,498 |
| Wall time per task median / p95 | 4.26 s / 9.98 s |
| Mean executor time per task | 8.47 ms |
| Tasks with unintended state changes | 0 |
| By category | aggregate 36/36, find 30/30, find-modify 12/12, modify 30/30, negative 18/18, read 24/24, scale 12/12, traverse 23/24 |
| Outcomes | answered 186 |
| Tool error codes | unknown-function 4, unknown-type 3, coercion-failed 2, invalid-plan 2, readonly-member 2, unknown-source 1, syntax-error 1 |
