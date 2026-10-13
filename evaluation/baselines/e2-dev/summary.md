### e2-row-ergonomics

model `deepseek-flash`, reasoning `none`, prompt `v1`, format `table`, toolset `rql`, schemaInPrompt `False`, maxSteps 12, maxRows 50

| Metric | Value |
|---|---:|
| Tasks (n) | 186 |
| Exact success | 181/186 = 97.3% (95% CI 93.9%–98.8%) |
| Total cost (provider-reported tokens × price snapshot) | $0.0722 |
| Cost / task | $0.00039 |
| Cost / successful task | $0.00040 |
| Successful tasks per $ | 2,508 |
| Successful tasks per wall-minute (sequential-equivalent) | 11.17 |
| Mean prompt / completion tokens per task | 9,820 / 351 (reasoning 0) |
| Prompt cache-hit ratio | 89.8% |
| Mean model turns / tool calls / tool errors | 4.45 / 3.93 / 0.06 |
| Protocol errors (turns without a tool call) | 0 |
| Mean tool-result chars per task | 4,397 |
| Wall time per task median / p95 | 4.56 s / 9.50 s |
| Mean executor time per task | 12.38 ms |
| Tasks with unintended state changes | 0 |
| By category | aggregate 34/36, find 30/30, find-modify 12/12, modify 30/30, negative 18/18, read 24/24, scale 12/12, traverse 21/24 |
| Outcomes | answered 184, max-steps 2 |
| Tool error codes | readonly-member 5, unknown-function 3, syntax-error 3, unknown-type 1 |
