### e6-schema-in-prompt

model `deepseek-flash`, reasoning `none`, prompt `v2`, format `table`, toolset `rql`, schemaInPrompt `True`, maxSteps 12, maxRows 50

| Metric | Value |
|---|---:|
| Tasks (n) | 186 |
| Exact success | 181/186 = 97.3% (95% CI 93.9%–98.8%) |
| Total cost (provider-reported tokens × price snapshot) | $0.0433 |
| Cost / task | $0.00023 |
| Cost / successful task | $0.00024 |
| Successful tasks per $ | 4,177 |
| Successful tasks per wall-minute (sequential-equivalent) | 18.71 |
| Mean prompt / completion tokens per task | 7,571 / 214 (reasoning 0) |
| Prompt cache-hit ratio | 92.7% |
| Mean model turns / tool calls / tool errors | 2.85 / 2.04 / 0.03 |
| Protocol errors (turns without a tool call) | 0 |
| Mean tool-result chars per task | 502 |
| Wall time per task median / p95 | 2.59 s / 6.49 s |
| Mean executor time per task | 3.70 ms |
| Tasks with unintended state changes | 0 |
| By category | aggregate 36/36, find 30/30, find-modify 12/12, modify 30/30, negative 18/18, read 24/24, scale 12/12, traverse 19/24 |
| Outcomes | answered 186 |
| Tool error codes | syntax-error 4, coercion-failed 2 |
