$body = @{
    model = "QuantTrio/Qwen3.5-9B-AWQ"
    messages = @(@{role="user"; content="Rispondi in italiano: qual è la capitale della Francia?"})
    max_tokens = 100
} | ConvertTo-Json -Depth 5

$response = Invoke-RestMethod -Uri "http://localhost:8000/v1/chat/completions" `
    -Method POST `
    -ContentType "application/json" `
    -Body $body `
    -TimeoutSec 60

$response | ConvertTo-Json -Depth 10 | Out-File -FilePath "C:\Users\User\Documents\ai\TinyGeneratorLC\vllm_response.json" -Encoding utf8
Write-Host "DONE"
