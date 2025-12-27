-- Insert Summarizer Agent using qwen2.5:7b-instruct
-- This agent generates concise summaries (3-5 sentences) of complete stories

-- First, get the model_id for qwen2.5:7b-instruct
-- Assuming the model is already in the database with a specific ID
-- We'll use a subquery to find it, or insert it if missing

-- Ensure qwen2.5:7b-instruct model exists
INSERT OR IGNORE INTO models (name, provider, context_length, created_at, updated_at, note)
VALUES ('qwen2.5:7b-instruct', 'ollama', 128000, datetime('now'), datetime('now'), 'Qwen 2.5 7B Instruct - Excellent for summarization with 128k context');

-- Insert the Summarizer agent
INSERT INTO agents (
    name, 
    role, 
    model_id,
    skills,
    prompt,
    instructions,
    is_active,
    created_at,
    updated_at,
    notes,
    temperature,
    top_p
)
SELECT 
    'Story Summarizer',
    'summarizer',
    (SELECT id FROM models WHERE name = 'qwen2.5:7b-instruct' LIMIT 1),
    '[]',
    'You are a professional story summarizer. Read the complete story and generate a concise summary.',
    'Read the entire story carefully and create a summary of 3-5 sentences that captures:
1. Main characters and their roles
2. The central conflict or problem
3. Key events in chronological order
4. The resolution (without major spoilers)

The summary should be:
- Concise but informative (3-5 sentences max)
- Engaging and encouraging readers to read the full story
- Written in the same language as the story (Italian for Italian stories)
- Free of spoilers for major plot twists
- Focused on the narrative arc

Output only the summary text, nothing else. No introductions, no formatting, just the summary.',
    1,
    datetime('now'),
    datetime('now'),
    'Summarizer agent using Qwen 2.5 7B with 128k context window',
    0.3,
    0.8
WHERE NOT EXISTS (SELECT 1 FROM agents WHERE role = 'summarizer' AND name = 'Story Summarizer');

-- Verify the insert
SELECT 
    a.id,
    a.name,
    a.role,
    m.name as model_name,
    a.is_active,
    a.created_at
FROM agents a
LEFT JOIN models m ON a.model_id = m.id
WHERE a.role = 'summarizer';
