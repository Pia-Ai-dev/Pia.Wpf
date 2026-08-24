# OpenRouter model context lengths

**Snapshot:** `GET https://openrouter.ai/api/v1/models`, taken 2026-08-24. Public endpoint, no API key.
**Origin:** asked in session while looking at `src/Pia.Wpf/Models/ContextWindowDefaults.cs`, whose doc comment claims no provider API Pia talks to reports a window. OpenRouter does.

422 models. Every one reports `context_length`; there were no nulls. Values are tokens.

Two caveats before treating a number here as a budget:

- `context_length` is what the model advertises. `top_provider.context_length` is what the default route actually serves, and it is different for 42 of them (second table).
- Endpoints for one model vary more than that. `GET /api/v1/models/{author}/{slug}/endpoints` breaks it down per host: `deepseek/deepseek-v4-flash-0731` is 262144 on OpenInference and 1048576 on Relace.

## All models

The ids sorting first under a leading `~` are OpenRouter alias rows (`~anthropic/claude-opus-latest`) that float to whatever the author ships as current, so their window can move without the id changing.

```
~anthropic/claude-fable-latest                              1000000
~anthropic/claude-haiku-latest                               200000
~anthropic/claude-opus-latest                               1000000
~anthropic/claude-sonnet-latest                             1000000
~deepseek/deepseek-v4-flash-latest                          1310720
~google/gemini-flash-latest                                 1048576
~google/gemini-pro-latest                                   1048576
~moonshotai/kimi-latest                                     1048576
~openai/gpt-latest                                          1050000
~openai/gpt-mini-latest                                      400000
~x-ai/grok-latest                                            500000
~z-ai/glm-latest                                            1048576
aion-labs/aion-2.0                                           131072
aion-labs/aion-3.0                                           131072
aion-labs/aion-3.0-mini                                      131072
aion-labs/aion-rp-llama-3.1-8b                                32768
allenai/olmo-3-32b-think                                      65536
amazon/nova-2-lite-v1                                       1000000
amazon/nova-lite-v1                                          300000
amazon/nova-micro-v1                                         128000
amazon/nova-premier-v1                                      1000000
amazon/nova-pro-v1                                           300000
anthracite-org/magnum-v4-72b                                  32768
anthropic/claude-3-haiku                                     200000
anthropic/claude-fable-5                                    1000000
anthropic/claude-fable-5:batch                              1000000
anthropic/claude-haiku-4.5                                   200000
anthropic/claude-haiku-4.5:batch                             200000
anthropic/claude-opus-4                                      200000
anthropic/claude-opus-4.1                                    200000
anthropic/claude-opus-4.1:batch                              200000
anthropic/claude-opus-4.5                                    200000
anthropic/claude-opus-4.5:batch                              200000
anthropic/claude-opus-4.6                                   1000000
anthropic/claude-opus-4.6:batch                             1000000
anthropic/claude-opus-4.7                                   1000000
anthropic/claude-opus-4.7-fast                              1000000
anthropic/claude-opus-4.7:batch                             1000000
anthropic/claude-opus-4.8                                   1000000
anthropic/claude-opus-4.8-fast                              1000000
anthropic/claude-opus-4.8:batch                             1000000
anthropic/claude-opus-5                                     1000000
anthropic/claude-opus-5-fast                                1000000
anthropic/claude-opus-5:batch                               1000000
anthropic/claude-sonnet-4                                   1000000
anthropic/claude-sonnet-4.5                                 1000000
anthropic/claude-sonnet-4.5:batch                           1000000
anthropic/claude-sonnet-4.6                                 1000000
anthropic/claude-sonnet-4.6:batch                           1000000
anthropic/claude-sonnet-5                                   1000000
anthropic/claude-sonnet-5:batch                             1000000
arcee-ai/trinity-large-thinking                              262144
arcee-ai/virtuoso-large                                      131072
baidu/ernie-4.5-vl-424b-a47b                                 123000
bytedance-seed/seed-1.6                                      262144
bytedance-seed/seed-1.6-flash                                262144
bytedance-seed/seed-2-1-turbo                                262144
bytedance-seed/seed-2.0-code                                 262144
bytedance-seed/seed-2.0-lite                                 262144
bytedance-seed/seed-2.0-mini                                 262144
bytedance/ui-tars-1.5-7b                                     128000
cognitivecomputations/dolphin-mistral-24b-venice-edition     128000
cohere/command-a                                             256000
cohere/command-r-08-2024                                     128000
cohere/command-r-plus-08-2024                                128000
cohere/command-r7b-12-2024                                   128000
cohere/north-mini-code:free                                  256000
deepseek/deepseek-chat                                       163840
deepseek/deepseek-chat-v3-0324                               163840
deepseek/deepseek-chat-v3.1                                  163840
deepseek/deepseek-r1                                          64000
deepseek/deepseek-r1-0528                                    163840
deepseek/deepseek-r1-distill-llama-70b                         8192
deepseek/deepseek-v3.1-terminus                              163840
deepseek/deepseek-v3.2                                       163840
deepseek/deepseek-v3.2-exp                                   163840
deepseek/deepseek-v4-flash                                  1048576
deepseek/deepseek-v4-flash-0731                             1310720
deepseek/deepseek-v4-flash-vision-exp                       1048576
deepseek/deepseek-v4-pro                                    1048576
deepseek/deepseek-v4-pro-0813                               1048576
dots-studio/dots-3-note-preview:free                         512000
google/gemini-2.5-flash                                     1048576
google/gemini-2.5-flash-image                                 32768
google/gemini-2.5-flash-lite                                1048576
google/gemini-2.5-flash-lite:batch                          1048576
google/gemini-2.5-flash:batch                               1048576
google/gemini-2.5-pro                                       1048576
google/gemini-2.5-pro-preview                               1048576
google/gemini-2.5-pro-preview-05-06                         1048576
google/gemini-2.5-pro:batch                                 1048576
google/gemini-3-flash-preview                               1048576
google/gemini-3-flash-preview:batch                         1048576
google/gemini-3-pro-image                                    131072
google/gemini-3-pro-image-preview                             65536
google/gemini-3.1-flash-image                                131072
google/gemini-3.1-flash-image-preview                         65536
google/gemini-3.1-flash-lite                                1048576
google/gemini-3.1-flash-lite-image                            65536
google/gemini-3.1-flash-lite-preview                        1048576
google/gemini-3.1-flash-lite:batch                          1048576
google/gemini-3.1-pro-preview                               1048576
google/gemini-3.1-pro-preview-customtools                   1048576
google/gemini-3.1-pro-preview:batch                         1048576
google/gemini-3.5-flash                                     1048576
google/gemini-3.5-flash-lite                                1048576
google/gemini-3.5-flash-lite:batch                          1048576
google/gemini-3.5-flash:batch                               1048576
google/gemini-3.6-flash                                     1048576
google/gemini-3.6-flash:batch                               1048576
google/gemini-3.7-flash                                     1048576
google/gemini-3.7-flash:batch                               1048576
google/gemma-2-27b-it                                          8192
google/gemma-3-12b-it                                        131072
google/gemma-3-27b-it                                        262144
google/gemma-3-4b-it                                         131072
google/gemma-3n-e4b-it                                        32768
google/gemma-4-26b-a4b-it                                    262144
google/gemma-4-26b-a4b-it:free                               262144
google/gemma-4-31b-it                                        262144
google/gemma-4-31b-it:free                                   262144
google/lyria-3-clip-preview                                 1048576
google/lyria-3-pro-preview                                  1048576
gryphe/mythomax-l2-13b                                         8192
ibm-granite/granite-4.0-h-micro                              131000
ibm-granite/granite-4.1-8b                                   131072
inception/mercury-2                                          128000
inclusionai/ling-2.6-1t                                      262144
inclusionai/ling-2.6-flash                                   262144
inclusionai/ling-3.0-flash                                   262144
inclusionai/ring-2.6-1t                                      262144
kwaipilot/kat-coder-air-v2.5                                 256000
kwaipilot/kat-coder-pro-v2                                   262144
kwaipilot/kat-coder-pro-v2.5                                 256000
liquid/lfm-2.5-2.6b:free                                      65536
mancer/weaver                                                  8000
meituan/longcat-2.0                                         1048756
meta-llama/llama-3.1-70b-instruct                            131072
meta-llama/llama-3.1-8b-instruct                             131072
meta-llama/llama-3.2-1b-instruct                              60000
meta-llama/llama-3.2-3b-instruct                             131072
meta-llama/llama-3.3-70b-instruct                            131072
meta-llama/llama-4-maverick                                 1048576
meta-llama/llama-4-scout                                    1310720
meta-llama/llama-guard-4-12b                                1048576
meta/muse-glimmer-30b                                        131072
meta/muse-spark-1.1                                         1048576
meta/muse-spark-1.2                                         1048576
meta/muse-spark-1.2-contributor                             1048576
microsoft/phi-4                                               16384
microsoft/wizardlm-2-8x22b                                    65535
minimax/minimax-01                                          1000192
minimax/minimax-m1                                          1000000
minimax/minimax-m2                                           204800
minimax/minimax-m2-her                                        65536
minimax/minimax-m2.1                                         204800
minimax/minimax-m2.5                                         204800
minimax/minimax-m2.7                                         204800
minimax/minimax-m3                                          1048576
minimax/minimax-m3:batch                                     524288
mistralai/codestral-2508                                     256000
mistralai/ministral-14b-2512                                 262144
mistralai/ministral-3b-2512                                  131072
mistralai/ministral-8b                                       128000
mistralai/ministral-8b-2512                                  262144
mistralai/mistral-large                                      128000
mistralai/mistral-large-2407                                 131072
mistralai/mistral-large-2512                                 262144
mistralai/mistral-medium-3                                   131072
mistralai/mistral-medium-3-5                                 262144
mistralai/mistral-medium-3.1                                 131072
mistralai/mistral-nemo                                       131072
mistralai/mistral-saba                                        32768
mistralai/mistral-small-24b-instruct-2501                     32768
mistralai/mistral-small-2603                                 262144
mistralai/mistral-small-3.1-24b-instruct                     128000
mistralai/mistral-small-3.2-24b-instruct                     131072
mistralai/mixtral-8x22b-instruct                              65536
mistralai/voxtral-small-24b-2507                              32000
moonshotai/kimi-k2                                           131072
moonshotai/kimi-k2-0905                                      262144
moonshotai/kimi-k2-thinking                                  262144
moonshotai/kimi-k2.5                                         262144
moonshotai/kimi-k2.6                                         262144
moonshotai/kimi-k2.7-code                                    262144
moonshotai/kimi-k2.7-code:batch                              262144
moonshotai/kimi-k3                                          1048576
morph/morph-v3-fast                                           81920
morph/morph-v3-large                                         262144
nex-agi/nex-n2-mini                                          262144
nex-agi/nex-n2-pro                                           262144
nousresearch/hermes-3-llama-3.1-405b                         131072
nousresearch/hermes-3-llama-3.1-70b                          131072
nousresearch/hermes-4-405b                                   131072
nousresearch/hermes-4-70b                                    131072
nvidia/nemotron-3-nano-30b-a3b                               262144
nvidia/nemotron-3-nano-30b-a3b:free                          256000
nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free           256000
nvidia/nemotron-3-super-120b-a12b                           1000000
nvidia/nemotron-3-super-120b-a12b:free                       262144
nvidia/nemotron-3-ultra-550b-a55b                            512288
nvidia/nemotron-3-ultra-550b-a55b:batch                      512288
nvidia/nemotron-3-ultra-550b-a55b:free                      1000000
nvidia/nemotron-3.5-content-safety:free                      128000
nvidia/nemotron-3.5-lightning                                262144
nvidia/nemotron-3.5-lightning:free                          1000000
nvidia/nemotron-nano-12b-v2-vl:free                          128000
nvidia/nemotron-nano-9b-v2:free                              128000
openai/gpt-3.5-turbo                                          16385
openai/gpt-3.5-turbo-0613                                      4095
openai/gpt-3.5-turbo-16k                                      16385
openai/gpt-3.5-turbo-instruct                                  4095
openai/gpt-3.5-turbo:batch                                    16385
openai/gpt-4                                                   8191
openai/gpt-4-turbo                                           128000
openai/gpt-4-turbo-preview                                   128000
openai/gpt-4-turbo:batch                                     128000
openai/gpt-4.1                                              1047576
openai/gpt-4.1-mini                                         1047576
openai/gpt-4.1-mini:batch                                   1047576
openai/gpt-4.1-nano                                         1047576
openai/gpt-4.1-nano:batch                                   1047576
openai/gpt-4.1:batch                                        1047576
openai/gpt-4o                                                128000
openai/gpt-4o-2024-05-13                                     128000
openai/gpt-4o-2024-08-06                                     128000
openai/gpt-4o-2024-11-20                                     128000
openai/gpt-4o-mini                                           128000
openai/gpt-4o-mini-2024-07-18                                128000
openai/gpt-4o-mini:batch                                     128000
openai/gpt-4o:batch                                          128000
openai/gpt-5                                                 400000
openai/gpt-5-codex:batch                                     400000
openai/gpt-5-image                                           400000
openai/gpt-5-image-mini                                      400000
openai/gpt-5-mini                                            400000
openai/gpt-5-mini:batch                                      400000
openai/gpt-5-nano                                            400000
openai/gpt-5-nano:batch                                      400000
openai/gpt-5-pro                                             400000
openai/gpt-5-pro:batch                                       400000
openai/gpt-5:batch                                           400000
openai/gpt-5.1                                               400000
openai/gpt-5.1-codex                                         400000
openai/gpt-5.1-codex-max                                     400000
openai/gpt-5.1-codex-mini                                    400000
openai/gpt-5.1:batch                                         400000
openai/gpt-5.2                                               400000
openai/gpt-5.2-chat                                          128000
openai/gpt-5.2-codex                                         400000
openai/gpt-5.2-pro                                           400000
openai/gpt-5.2-pro:batch                                     400000
openai/gpt-5.2:batch                                         400000
openai/gpt-5.3-codex                                         400000
openai/gpt-5.4                                              1050000
openai/gpt-5.4-image-2                                       272000
openai/gpt-5.4-mini                                          400000
openai/gpt-5.4-mini:batch                                    400000
openai/gpt-5.4-nano                                          400000
openai/gpt-5.4-nano:batch                                    400000
openai/gpt-5.4-pro                                          1050000
openai/gpt-5.4-pro:batch                                    1050000
openai/gpt-5.4:batch                                        1050000
openai/gpt-5.5                                              1050000
openai/gpt-5.5-pro                                          1050000
openai/gpt-5.5-pro:batch                                    1050000
openai/gpt-5.5:batch                                        1050000
openai/gpt-5.6-luna                                         1050000
openai/gpt-5.6-luna-pro                                     1050000
openai/gpt-5.6-luna-pro:batch                               1050000
openai/gpt-5.6-luna:batch                                   1050000
openai/gpt-5.6-sol                                          1050000
openai/gpt-5.6-sol-pro                                      1050000
openai/gpt-5.6-sol-pro:batch                                1050000
openai/gpt-5.6-sol:batch                                    1050000
openai/gpt-5.6-terra                                        1050000
openai/gpt-5.6-terra-pro                                    1050000
openai/gpt-5.6-terra-pro:batch                              1050000
openai/gpt-5.6-terra:batch                                  1050000
openai/gpt-audio                                             128000
openai/gpt-audio-mini                                        128000
openai/gpt-chat-latest                                       400000
openai/gpt-oss-120b                                          131072
openai/gpt-oss-20b                                           131072
openai/gpt-oss-safeguard-20b                                 131072
openai/o1                                                    200000
openai/o1-pro                                                200000
openai/o1-pro:batch                                          200000
openai/o1:batch                                              200000
openai/o3                                                    200000
openai/o3-mini                                               200000
openai/o3-mini-high                                          200000
openai/o3-mini-high:batch                                    200000
openai/o3-mini:batch                                         200000
openai/o3-pro                                                200000
openai/o3-pro:batch                                          200000
openai/o3:batch                                              200000
openai/o4-mini                                               200000
openai/o4-mini-high                                          200000
openai/o4-mini-high:batch                                    200000
openai/o4-mini:batch                                         200000
openrouter/auto                                             2000000
openrouter/auto-beta                                        2000000
openrouter/bodybuilder                                       128000
openrouter/free                                              200000
openrouter/fusion                                           1000000
openrouter/pareto-code                                      2000000
perceptron/perceptron-mk1                                     32768
perplexity/sonar                                             127072
perplexity/sonar-deep-research                               128000
perplexity/sonar-pro                                         200000
perplexity/sonar-pro-search                                  200000
perplexity/sonar-reasoning-pro                               128000
poolside/laguna-s-2.1                                       1048576
poolside/laguna-s-2.1:free                                   262144
poolside/laguna-xs-2.1                                       262144
poolside/laguna-xs-2.1:free                                  262144
qwen/qwen-2.5-72b-instruct                                    32768
qwen/qwen-2.5-7b-instruct                                     32768
qwen/qwen-2.5-coder-32b-instruct                              32768
qwen/qwen-plus                                              1000000
qwen/qwen-plus-2025-07-28                                   1000000
qwen/qwen-plus-2025-07-28:thinking                          1000000
qwen/qwen2.5-vl-72b-instruct                                 128000
qwen/qwen3-14b                                               131072
qwen/qwen3-235b-a22b                                         131072
qwen/qwen3-235b-a22b-2507                                    262144
qwen/qwen3-235b-a22b-thinking-2507                           262144
qwen/qwen3-30b-a3b                                           131072
qwen/qwen3-30b-a3b-instruct-2507                             262144
qwen/qwen3-30b-a3b-thinking-2507                              81920
qwen/qwen3-32b                                               131072
qwen/qwen3-8b                                                131072
qwen/qwen3-coder                                             262144
qwen/qwen3-coder-30b-a3b-instruct                            262144
qwen/qwen3-coder-flash                                      1000000
qwen/qwen3-coder-next                                        262144
qwen/qwen3-coder-plus                                       1000000
qwen/qwen3-max                                               262144
qwen/qwen3-max-thinking                                      262144
qwen/qwen3-next-80b-a3b-instruct                             262144
qwen/qwen3-next-80b-a3b-thinking                             262144
qwen/qwen3-vl-235b-a22b-instruct                             262144
qwen/qwen3-vl-235b-a22b-thinking                             131072
qwen/qwen3-vl-30b-a3b-instruct                               262144
qwen/qwen3-vl-30b-a3b-thinking                               262144
qwen/qwen3-vl-32b-instruct                                   131072
qwen/qwen3-vl-8b-instruct                                    262144
qwen/qwen3-vl-8b-thinking                                    131072
qwen/qwen3.5-122b-a10b                                       262144
qwen/qwen3.5-27b                                             262144
qwen/qwen3.5-35b-a3b                                         262144
qwen/qwen3.5-397b-a17b                                       262144
qwen/qwen3.5-9b                                              262144
qwen/qwen3.5-flash-02-23                                    1000000
qwen/qwen3.5-plus-02-15                                     1000000
qwen/qwen3.5-plus-20260420                                  1000000
qwen/qwen3.6-27b                                             262144
qwen/qwen3.6-35b-a3b                                         262144
qwen/qwen3.6-flash                                          1000000
qwen/qwen3.6-max-preview                                     262144
qwen/qwen3.6-plus                                           1000000
qwen/qwen3.7-flash                                          1000000
qwen/qwen3.7-max                                            1000000
qwen/qwen3.7-plus                                           1000000
qwen/qwen3.8-2.4t-a95b                                      1048576
qwen/qwen3.8-27b                                            1000000
qwen/qwen3.8-max                                            1000000
rekaai/reka-edge                                              16384
rekaai/reka-flash-3                                           65536
relace/relace-apply-3                                        256000
relace/relace-search                                         256000
sakana/fugu-ultra                                           1000000
sakana/sakana-namazu                                         262144
sao10k/l3-lunaris-8b                                           8192
sao10k/l3.1-euryale-70b                                      131072
sao10k/l3.3-euryale-70b                                      131072
stealth/ox-alpha                                            1048576
stepfun/step-3.5-flash                                       262144
stepfun/step-3.7-flash                                       262144
tencent/hunyuan-a13b-instruct                                131072
tencent/hy-mt2-1.8b                                            8192
tencent/hy-mt2-30b-a3b                                         8192
tencent/hy-mt2-7b                                              8192
tencent/hy3                                                  262144
tencent/hy3-preview                                          262144
thedrummer/cydonia-24b-v4.1                                  131072
thedrummer/rocinante-12b                                      65536
thedrummer/skyfall-36b-v2                                     32768
thedrummer/unslopnemo-12b                                   1024000
thinkingmachines/inkling                                    1048576
thinkingmachines/inkling-small                              1048576
thinkingmachines/inkling-small:free                          262144
thinkingmachines/inkling:batch                               524288
thinkingmachines/inkling:free                                262144
undi95/remm-slerp-l2-13b                                       6144
upstage/solar-pro-3                                          131072
upstage/solar-pro4                                           524288
writer/palmyra-x5                                           1040000
x-ai/grok-4.20                                              2000000
x-ai/grok-4.20-multi-agent                                  2000000
x-ai/grok-4.3                                               1000000
x-ai/grok-4.5                                                500000
x-ai/grok-4.6                                                500000
x-ai/grok-build-0.1                                          256000
xiaomi/mimo-v2.5                                            1050000
xiaomi/mimo-v2.5-pro                                        1050000
z-ai/glm-4.5                                                 131072
z-ai/glm-4.5-air                                             131072
z-ai/glm-4.5v                                                 65536
z-ai/glm-4.6                                                 204800
z-ai/glm-4.6v                                                131072
z-ai/glm-4.7                                                 204800
z-ai/glm-4.7-flash                                           202752
z-ai/glm-5                                                   204800
z-ai/glm-5-turbo                                             202752
z-ai/glm-5.1                                                 204800
z-ai/glm-5.2                                                1048576
z-ai/glm-5.2:batch                                          1048575
z-ai/glm-5.2:free                                            256000
z-ai/glm-5.3                                                1048576
z-ai/glm-5v-turbo                                            202752
```

## Where the default route serves something other than the advertised window

```
                                                           advertised  top_provider
~deepseek/deepseek-v4-flash-latest                            1310720       1048576
~moonshotai/kimi-latest                                       1048576        974842
anthropic/claude-sonnet-4                                     1000000        200000
deepseek/deepseek-chat                                         163840        128000
deepseek/deepseek-chat-v3.1                                    163840        161000
deepseek/deepseek-v3.1-terminus                                163840        131072
deepseek/deepseek-v4-flash                                    1048576       1024000
deepseek/deepseek-v4-flash-0731                               1310720       1048576
deepseek/deepseek-v4-pro                                      1048576       1024000
deepseek/deepseek-v4-pro-0813                                 1048576       1048575
google/gemini-3-pro-image                                      131072         65536
google/gemma-3-27b-it                                          262144        131072
gryphe/mythomax-l2-13b                                           8192          4096
kwaipilot/kat-coder-pro-v2                                     262144        256000
meta-llama/llama-4-scout                                      1310720        327680
meta-llama/llama-guard-4-12b                                  1048576        163840
minimax/minimax-m2.5                                           204800        200000
minimax/minimax-m2.7                                           204800        196608
minimax/minimax-m3                                            1048576        524288
mistralai/mistral-small-3.2-24b-instruct                       131072        128000
nvidia/nemotron-3-super-120b-a12b                             1000000        262144
qwen/qwen3-14b                                                 131072         40960
qwen/qwen3-235b-a22b-thinking-2507                             262144        131072
qwen/qwen3-30b-a3b                                             131072         40960
qwen/qwen3-30b-a3b-instruct-2507                               262144        128000
qwen/qwen3-32b                                                 131072         40960
qwen/qwen3-next-80b-a3b-thinking                               262144        131072
qwen/qwen3-vl-235b-a22b-instruct                               262144        131072
qwen/qwen3-vl-30b-a3b-instruct                                 262144        131072
qwen/qwen3-vl-30b-a3b-thinking                                 262144        131072
qwen/qwen3-vl-8b-instruct                                      262144        131072
qwen/qwen3.8-27b                                              1000000        262144
stepfun/step-3.7-flash                                         262144        256000
thedrummer/unslopnemo-12b                                     1024000         32768
thinkingmachines/inkling                                      1048576        524288
thinkingmachines/inkling-small                                1048576        524288
xiaomi/mimo-v2.5                                              1050000       1048576
xiaomi/mimo-v2.5-pro                                          1050000       1048576
z-ai/glm-4.6                                                   204800        202752
z-ai/glm-4.7                                                   204800        202752
z-ai/glm-5                                                     204800        198000
z-ai/glm-5.1                                                   204800        200000
```

## Refreshing this

```
curl -s https://openrouter.ai/api/v1/models > or-models.json
```

Each entry also carries `pricing`, `supported_parameters`, `architecture.tokenizer` and `architecture.input_modalities`, none of which are reproduced here.
