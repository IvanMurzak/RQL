### e3-scope-escapes

model `deepseek-flash`, reasoning `none`, prompt `v1`, format `table`, toolset `rql`, schemaInPrompt `False`, maxSteps 12, maxRows 50

| Metric | Value |
|---|---:|
| Tasks (n) | 186 |
| Exact success | 186/186 = 100.0% (95% CI 98.0%–100.0%) |
| Total cost (provider-reported tokens × price snapshot) | $0.0726 |
| Cost / task | $0.00039 |
| Cost / successful task | $0.00039 |
| Successful tasks per $ | 2,562 |
| Successful tasks per wall-minute (sequential-equivalent) | 11.63 |
| Mean prompt / completion tokens per task | 9,785 / 347 (reasoning 0) |
| Prompt cache-hit ratio | 89.4% |
| Mean model turns / tool calls / tool errors | 4.48 / 3.87 / 0.08 |
| Protocol errors (turns without a tool call) | 0 |
| Mean tool-result chars per task | 4,527 |
| Wall time per task median / p95 | 5.01 s / 9.50 s |
| Mean executor time per task | 11.26 ms |
| Tasks with unintended state changes | 0 |
| By category | aggregate 36/36, find 30/30, find-modify 12/12, modify 30/30, negative 18/18, read 24/24, scale 12/12, traverse 24/24 |
| Outcomes | answered 186 |
| Tool error codes | readonly-member 6, unknown-function 4, syntax-error 4 |
