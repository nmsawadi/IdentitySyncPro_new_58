/* ============================================================================
   RECOVERY — hashes written by a dry run (incident 2026-07-20)
   ----------------------------------------------------------------------------
   WHY
   A dry run used to stamp the current hash into SyncStates. On a fresh database
   the dry run ran first and wrote 111,465 rows, so the full sync that followed
   compared hashes, found them identical, reported NoChange for every identity
   and pushed nothing to Active Directory.

   The code is fixed (a dry run no longer writes state), but the rows it already
   wrote still claim "already synced". Until their hashes are cleared, every
   future sync will keep skipping them.

   WHAT THIS DOES
   Blanks CurrentHash so the next Full Sync sees every identity as changed and
   re-evaluates it against AD. It does NOT delete rows and does NOT touch AD.
   Safe Sync still applies: the sync only creates, updates attributes, moves
   OUs and adjusts groups — it never deletes or disables an account.

   HOW TO RUN
   Steps 1-2 are read-only; run them first and read the output.
   Step 3 is the change — it is wrapped in a transaction that is NOT committed
   until you run step 4 yourself, after checking the affected row count.

   Run against the SYSTEM database (IdentitySyncProDB), not Oracle.
   ============================================================================ */

/* --- STEP 1 (read-only): which tenants are affected, and how badly? --------- */
SELECT
    t.Id                AS TenantId,
    t.TenantName,
    COUNT(s.Id)         AS TotalRows,
    SUM(CASE WHEN s.CurrentHash IS NOT NULL AND s.CurrentHash <> '' THEN 1 ELSE 0 END) AS RowsWithHash,
    MIN(s.LastSyncDate) AS OldestLastSync,
    MAX(s.LastSyncDate) AS NewestLastSync
FROM SyncStates s
JOIN TenantSettings t ON t.Id = s.TenantId
GROUP BY t.Id, t.TenantName
ORDER BY t.Id;

/* --- STEP 2 (read-only): confirm the dry run is the source -----------------
   A DryRun row here whose EndTime lines up with the LastSyncDate above is the
   confirmation. RunType 'DryRun' = full dry run, 'DryRun-Single' = single.     */
SELECT TOP 20
    Id, TenantId, RunType, Status, StartTime, EndTime,
    TotalProcessed, TotalCreated, TotalUpdated, TotalNoChange
FROM SyncRuns
ORDER BY StartTime DESC;

/* --- STEP 3: clear the hashes ---------------------------------------------
   Set @TenantId to the tenant from step 1 (do NOT leave it NULL unless you
   really intend to reset every tenant).                                       */

BEGIN TRANSACTION;

DECLARE @TenantId INT = 1;   -- <<< set this

UPDATE SyncStates
SET CurrentHash = '',        -- '' never matches a real SHA256 → forces re-check
    LastModified = SYSUTCDATETIME()
WHERE (@TenantId IS NULL OR TenantId = @TenantId);

SELECT @@ROWCOUNT AS RowsAffected;   -- expect ~111465 for the incident tenant

/* --- STEP 4: decide -------------------------------------------------------
   RowsAffected looks right  ->  run:  COMMIT TRANSACTION;
   Anything unexpected       ->  run:  ROLLBACK TRANSACTION;

   Nothing is persisted until you run one of these two.
   ============================================================================ */

/* --- AFTER COMMITTING -----------------------------------------------------
   1. Deploy the fixed build first (otherwise a later dry run re-poisons them).
   2. Run a FULL SYNC (not a dry run).
   3. Expect Updated to be large and NoChange ~0 on that run — that is the
      backlog finally being applied. Subsequent runs settle back to mostly
      NoChange, which is the normal steady state.
   ============================================================================ */
