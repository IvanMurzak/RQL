### baseline

model `deepseek-flash`, reasoning `none`, prompt `v2`, format `table`, toolset `rql`, schemaInPrompt `False`, maxSteps 12, maxRows 50

| Metric | Value |
|---|---:|
| Tasks (n) | 186 |
| Exact success | 186/186 = 100.0% (95% CI 98.0%–100.0%) |
| Total cost (provider-reported tokens × price snapshot) | $0.0703 |
| Cost / task | $0.00038 |
| Cost / successful task | $0.00038 |
| Successful tasks per $ | 2,644 |
| Successful tasks per wall-minute (sequential-equivalent) | 14.50 |
| Mean prompt / completion tokens per task | 7,870 / 287 (reasoning 0) |
| Prompt cache-hit ratio | 84.2% |
| Mean model turns / tool calls / tool errors | 3.88 / 3.40 / 0.04 |
| Protocol errors (turns without a tool call) | 0 |
| Mean tool-result chars per task | 3,708 |
| Wall time per task median / p95 | 3.79 s / 7.53 s |
| Mean executor time per task | 9.92 ms |
| Tasks with unintended state changes | 0 |
| By category | aggregate 36/36, find 30/30, find-modify 12/12, modify 30/30, negative 18/18, read 24/24, scale 12/12, traverse 24/24 |
| Outcomes | answered 186 |
| Tool error codes | unknown-type 6, readonly-member 1 |
