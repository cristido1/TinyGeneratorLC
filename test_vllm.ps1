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

$response.choices[0].message.content
