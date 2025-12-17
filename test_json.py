import json

# Test JSON - as provided by user
json_str = '''[
    {
      "name": "Narratore",
      "gender": "male"
    },
    {
      "name": "CARTA",
      "gender": "male",
      "title": "comandante",
      "age": 55
    },
    {
      "name": "ELENA ROSSI",
      "gender": "female",
      "title": "ufficiale",
      "age": 40
    }
]'''

try:
    data = json.loads(json_str)
    print(f"JSON valid, {len(data)} characters")
    for c in data:
        print(f"  - {c.get('name')}: age={c.get('age')} (type: {type(c.get('age')).__name__})")
except Exception as e:
    print(f"JSON parse error: {e}")
