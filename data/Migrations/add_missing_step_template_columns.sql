-- Migration: Add missing columns to step_templates table
-- Date: 2024-12-27
-- Purpose: Fix schema drift from backup restoration

-- Add min_chars_trama column
ALTER TABLE step_templates 
ADD COLUMN min_chars_trama INTEGER;

-- Add min_chars_story column
ALTER TABLE step_templates 
ADD COLUMN min_chars_story INTEGER;

-- Add full_story_step column (fixes immediate error)
ALTER TABLE step_templates 
ADD COLUMN full_story_step INTEGER;
