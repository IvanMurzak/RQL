### baseline

model `deepseek-flash`, reasoning `none`, prompt `v1`, format `table`, toolset `rql`, schemaInPrompt `False`, maxSteps 12, maxRows 50

| Metric | Value |
|---|---:|
| Tasks (n) | 186 |
| Exact success | 169/186 = 90.9% (95% CI 85.9%–94.2%) |
| Total cost (provider-reported tokens × price snapshot) | $0.0881 |
| Cost / task | $0.00047 |
| Cost / successful task | $0.00052 |
| Successful tasks per $ | 1,919 |
| Successful tasks per wall-minute (sequential-equivalent) | 9.82 |
| Mean prompt / completion tokens per task | 11,032 / 388 (reasoning 0) |
| Prompt cache-hit ratio | 87.2% |
| Mean model turns / tool calls / tool errors | 4.90 / 4.41 / 0.12 |
| Protocol errors (turns without a tool call) | 0 |
| Mean tool-result chars per task | 4,453 |
| Wall time per task median / p95 | 4.69 s / 12.17 s |
| Mean executor time per task | 11.14 ms |
| Tasks with unintended state changes | 0 |
| By category | aggregate 23/36, find 30/30, find-modify 12/12, modify 30/30, negative 18/18, read 24/24, scale 12/12, traverse 20/24 |
| Outcomes | answered 182, max-steps 4 |
| Tool error codes | syntax-error 7, unknown-type 6, readonly-member 5, unknown-function 4 |
