import urllib.request, json, sys

url = "http://localhost:8000/v1/chat/completions"
payload = {
    "model": "QuantTrio/Qwen3.5-9B-AWQ",
    "messages": [{"role": "user", "content": "Rispondi in italiano: qual è la capitale della Francia?"}],
    "max_tokens": 100
}
data = json.dumps(payload).encode()
req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
try:
    with urllib.request.urlopen(req, timeout=60) as resp:
        result = json.loads(resp.read())
        print(result["choices"][0]["message"]["content"])
except Exception as e:
    print(f"Errore: {e}", file=sys.stderr)
