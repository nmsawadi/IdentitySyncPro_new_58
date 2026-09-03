-- =============================================
-- Migration: Add SMS Notification Settings
-- Run this on your IdentitySyncPro database
-- =============================================

-- Add SMS columns to TenantSettings table
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'EnableSmsNotification')
BEGIN
    ALTER TABLE TenantSettings ADD EnableSmsNotification BIT NOT NULL DEFAULT 0;
    ALTER TABLE TenantSettings ADD SmsApiUrl NVARCHAR(500) NOT NULL DEFAULT '';
    ALTER TABLE TenantSettings ADD SmsSenderName NVARCHAR(100) NOT NULL DEFAULT '';
    ALTER TABLE TenantSettings ADD SmsApiUsername NVARCHAR(200) NOT NULL DEFAULT '';
    ALTER TABLE TenantSettings ADD SmsApiPassword NVARCHAR(200) NOT NULL DEFAULT '';
    ALTER TABLE TenantSettings ADD SmsMessageTemplate NVARCHAR(MAX) NOT NULL DEFAULT N'مرحباً {STUDENT_NAME}، تم إنشاء حسابك الجامعي.
اسم المستخدم: {USERNAME}
كلمة المرور: {PASSWORD}';
    
    PRINT 'SMS columns added successfully.';
END
ELSE
BEGIN
    PRINT 'SMS columns already exist.';
END
GO
