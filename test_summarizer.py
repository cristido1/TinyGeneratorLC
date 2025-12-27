#!/usr/bin/env python3
"""
Test script per invocare comandi di summarizzazione via API.

Usage:
    python test_summarizer.py <story_id>           # Singola storia
    python test_summarizer.py --batch [min_score]  # Batch per storie >= min_score

Examples:
    python test_summarizer.py 123
    python test_summarizer.py --batch
    python test_summarizer.py --batch 70
"""
import sys
import requests
import time

def summarize_story(story_id, base_url="http://localhost:5000"):
    """
    Invoca l'endpoint di summarizzazione per una singola storia.
    """
    print(f"📝 Requesting summary for story {story_id}...")
    
    # POST /api/commands/summarize?storyId=123
    response = requests.post(f"{base_url}/api/commands/summarize", params={"storyId": story_id})
    
    if response.status_code != 200:
        print(f"❌ Error: {response.status_code}")
        print(response.text)
        return False
    
    data = response.json()
    run_id = data.get("runId")
    print(f"✓ Summarization enqueued (runId: {run_id})")
    
    # Poll per verifica completamento (opzionale)
    print("⏳ Waiting for completion...")
    for i in range(30):  # Max 30 secondi
        time.sleep(1)
        
        # Controlla se il comando è ancora attivo
        status_response = requests.get(f"{base_url}/api/commands")
        if status_response.status_code == 200:
            active_commands = status_response.json()
            still_running = any(cmd.get("runId") == run_id for cmd in active_commands)
            
            if not still_running:
                print(f"✓ Summary generated after {i+1} seconds")
                return True
        
        if (i + 1) % 5 == 0:
            print(f"  ... still waiting ({i+1}s)")
    
    print("⚠ Timeout - check logs for details")
    return False

def batch_summarize(min_score=60, base_url="http://localhost:5000"):
    """
    Invoca l'endpoint di batch summarizzazione.
    Accoda N comandi e ritorna immediatamente.
    """
    print(f"📚 Requesting batch summarization (min score: {min_score})...")
    
    # POST /api/commands/batch-summarize?minScore=60
    response = requests.post(f"{base_url}/api/commands/batch-summarize", params={"minScore": min_score})
    
    if response.status_code != 200:
        print(f"❌ Error: {response.status_code}")
        print(response.text)
        return False
    
    data = response.json()
    run_id = data.get("runId")
    print(f"✓ Batch summarization started (runId: {run_id})")
    print("ℹ️  Il comando batch terminerà subito dopo aver accodato i riassunti individuali")
    
    # Aspetta solo che il comando batch termini (veloce)
    print("⏳ Waiting for batch command to complete...")
    for i in range(10):
        time.sleep(1)
        
        status_response = requests.get(f"{base_url}/api/commands")
        if status_response.status_code == 200:
            active_commands = status_response.json()
            batch_cmd = next((cmd for cmd in active_commands if cmd.get("runId") == run_id), None)
            
            if batch_cmd:
                status = batch_cmd.get("status", "").lower()
                if status == "completed":
                    print(f"✓ Batch command completed")
                    print(f"ℹ️  I comandi SummarizeStory individuali sono ora in coda")
                    
                    # Conta quanti SummarizeStory sono in coda
                    summarize_cmds = [cmd for cmd in active_commands 
                                     if cmd.get("operationName") == "SummarizeStory"
                                     and cmd.get("metadata", {}).get("triggeredBy") == "batch_summarize"]
                    print(f"📊 Found {len(summarize_cmds)} SummarizeStory commands in queue")
                    return True
                elif status == "failed":
                    print(f"❌ Batch command failed: {batch_cmd.get('errorMessage', 'Unknown error')}")
                    return False
    
    print("⚠ Timeout waiting for batch command")
    return False

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage:")
        print("  python test_summarizer.py <story_id>           # Singola storia")
        print("  python test_summarizer.py --batch [min_score]  # Batch")
        print("\nExamples:")
        print("  python test_summarizer.py 123")
        print("  python test_summarizer.py --batch")
        print("  python test_summarizer.py --batch 70")
        sys.exit(1)
    
    if sys.argv[1] == "--batch":
        min_score = int(sys.argv[2]) if len(sys.argv) > 2 else 60
        batch_summarize(min_score)
    else:
        story_id = int(sys.argv[1])
        summarize_story(story_id)
