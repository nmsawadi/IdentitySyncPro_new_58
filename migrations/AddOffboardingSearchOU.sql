-- =============================================
-- Migration: Add OffboardingSearchOU column
-- Allows specifying a specific OU to search in during offboarding
-- instead of searching the entire domain (ADBaseDN).
-- Run this on your IdentitySyncPro database.
-- =============================================

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'OffboardingSearchOU')
BEGIN
    ALTER TABLE Svc_Services ADD OffboardingSearchOU NVARCHAR(500) NULL;
    PRINT 'OffboardingSearchOU column added successfully.';
END
ELSE
BEGIN
    PRINT 'OffboardingSearchOU column already exists.';
END
GO
