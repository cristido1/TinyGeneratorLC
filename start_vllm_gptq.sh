#!/bin/bash
MSYS_NO_PATHCONV=1 docker run -d --name tinygenerator-vllm --gpus all \
  -p 8000:8000 \
  -v "C:/vllm_models:/models" \
  vllm/vllm-openai:nightly \
  /models/Vishva007--Qwen3.5-9B-W4A16-AutoRound-GPTQ \
  --tokenizer /models/QuantTrio--Qwen3.5-9B-AWQ \
  --host 0.0.0.0 \
  --port 8000 \
  --served-model-name QuantTrio/Qwen3.5-9B-AWQ \
  --gpu-memory-utilization 0.90 \
  --max-model-len 4096 \
  --max-num-seqs 1 \
  --max-num-batched-tokens 2048 \
  --trust-remote-code \
  --no-enable-log-requests
