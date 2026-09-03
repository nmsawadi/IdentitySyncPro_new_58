namespace IdentitySyncPro.Core.Enums
{
    public enum ConnectorType
    {
        Source = 0,
        Target = 1
    }

    public enum OperationType
    {
        Create = 0,
        Update = 1,
        Delete = 2,
        Disable = 3,
        Enable = 4,
        Sync = 5,
        Skip = 6,
        Move = 7,
        GroupRemoval = 8
    }

    public enum SyncDirection
    {
        SourceToTarget = 0,
        TargetToSource = 1,
        Bidirectional = 2
    }

    public enum SyncRunStatus
    {
        Pending = 0,
        Running = 1,
        Completed = 2,
        CompletedWithErrors = 3,
        Failed = 4,
        Cancelled = 5
    }

    public enum SyncOperationStatus
    {
        Success = 0,
        Failed = 1,
        Skipped = 2,
        NoChange = 3
    }

    public enum ConnectorStatus
    {
        Connected = 0,
        Disconnected = 1,
        Error = 2,
        Testing = 3
    }

    public enum AuditSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2,
        Critical = 3
    }

    public enum DatabaseProviderType
    {
        SqlServer = 0,
        Oracle = 1,
        PostgreSQL = 2,
        MySQL = 3
    }

    public enum ScheduleMode
    {
        Interval = 0,
        Daily = 1,
        Weekly = 2,
        Custom = 3
    }

}
