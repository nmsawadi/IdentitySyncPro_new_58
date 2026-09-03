-- Migration: Add LastStatusCode to SyncStates
-- Purpose: Enable status change detection during sync to trigger lifecycle rules
-- Date: 2026-06-29
-- 
-- This column stores the student's last known StatusCode from Oracle.
-- Used to detect transitions like Active→Graduate, Withdrawn→Active, etc.
-- When a status change is detected, lifecycle rules are triggered to:
--   - Move student to the correct OU
--   - Add/remove AD groups as needed

ALTER TABLE SyncStates ADD LastStatusCode INT NULL;
GO

-- Backfill: Set LastStatusCode for existing records based on current status
-- This prevents false-positive status change detection on the first sync after migration
-- Run this AFTER the ALTER TABLE above
-- 
-- Option 1: If you want lifecycle to re-evaluate ALL students on next sync, skip this step
-- Option 2: If you want to avoid re-processing, uncomment and run:
-- UPDATE SyncStates SET LastStatusCode = 1 WHERE Status = 'Synced';
