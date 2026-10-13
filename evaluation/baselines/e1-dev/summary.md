### e1-exec-ergonomics

model `deepseek-flash`, reasoning `none`, prompt `v1`, format `table`, toolset `rql`, schemaInPrompt `False`, maxSteps 12, maxRows 50

| Metric | Value |
|---|---:|
| Tasks (n) | 186 |
| Exact success | 183/186 = 98.4% (95% CI 95.4%–99.4%) |
| Total cost (provider-reported tokens × price snapshot) | $0.0764 |
| Cost / task | $0.00041 |
| Cost / successful task | $0.00042 |
| Successful tasks per $ | 2,395 |
| Successful tasks per wall-minute (sequential-equivalent) | 10.75 |
| Mean prompt / completion tokens per task | 10,345 / 366 (reasoning 0) |
| Prompt cache-hit ratio | 89.5% |
| Mean model turns / tool calls / tool errors | 4.65 / 4.14 / 0.06 |
| Protocol errors (turns without a tool call) | 0 |
| Mean tool-result chars per task | 4,372 |
| Wall time per task median / p95 | 4.84 s / 10.53 s |
| Mean executor time per task | 10.80 ms |
| Tasks with unintended state changes | 0 |
| By category | aggregate 36/36, find 30/30, find-modify 12/12, modify 30/30, negative 18/18, read 23/24, scale 12/12, traverse 22/24 |
| Outcomes | answered 183, max-steps 3 |
| Tool error codes | readonly-member 6, unknown-function 3, syntax-error 2 |
