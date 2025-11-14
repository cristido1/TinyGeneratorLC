BEGIN TRANSACTION;

-- Populate allowed_plugins in test_definitions when empty
UPDATE test_definitions
SET allowed_plugins = (
  CASE
    WHEN allowed_plugins IS NOT NULL AND trim(allowed_plugins) <> '' THEN allowed_plugins
    WHEN library IS NULL OR trim(library) = '' THEN 'text'
    WHEN lower(library) LIKE '%audiocraft%' OR lower(library) LIKE '%audio%' THEN 'audiocraft,http'
    WHEN lower(library) LIKE '%tts%' THEN 'tts,http'
    WHEN lower(library) LIKE '%memory%' THEN 'memory'
    WHEN lower(library) LIKE '%http%' THEN 'http'
    WHEN lower(library) LIKE '%filesystem%' OR lower(library) LIKE '%file%' THEN 'filesystem'
    WHEN lower(library) LIKE '%math%' THEN 'math'
    WHEN lower(library) LIKE '%text%' THEN 'text'
    WHEN lower(library) LIKE '%time%' THEN 'time'
    ELSE trim(library)
  END
)
WHERE allowed_plugins IS NULL OR trim(allowed_plugins) = '';

-- Do the same for test_prompts if present
UPDATE test_prompts
SET allowed_plugins = (
  CASE
    WHEN allowed_plugins IS NOT NULL AND trim(allowed_plugins) <> '' THEN allowed_plugins
    WHEN library IS NULL OR trim(library) = '' THEN 'text'
    WHEN lower(library) LIKE '%audiocraft%' OR lower(library) LIKE '%audio%' THEN 'audiocraft,http'
    WHEN lower(library) LIKE '%tts%' THEN 'tts,http'
    WHEN lower(library) LIKE '%memory%' THEN 'memory'
    WHEN lower(library) LIKE '%http%' THEN 'http'
    WHEN lower(library) LIKE '%filesystem%' OR lower(library) LIKE '%file%' THEN 'filesystem'
    WHEN lower(library) LIKE '%math%' THEN 'math'
    WHEN lower(library) LIKE '%text%' THEN 'text'
    WHEN lower(library) LIKE '%time%' THEN 'time'
    ELSE trim(library)
  END
)
WHERE (SELECT name FROM sqlite_master WHERE type='table' AND name='test_prompts') IS NOT NULL
  AND (allowed_plugins IS NULL OR trim(allowed_plugins) = '');

COMMIT;

-- Show a few sample rows to confirm
.mode column
.headers on
SELECT id, group_name, library, allowed_plugins FROM test_definitions ORDER BY id LIMIT 30;

-- If test_prompts exists, show some rows
SELECT name FROM sqlite_master WHERE type='table' AND name='test_prompts';
SELECT id, group_name, library, allowed_plugins FROM test_prompts ORDER BY id LIMIT 30;
