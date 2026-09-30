-- شغّل السكربت ده مرة واحدة على الداتابيز (EnsureCreated مبيضيفش أعمدة لجداول موجودة).
-- آمن لو اتشغل أكتر من مرة.

IF COL_LENGTH('dbo.TrainStops', 'DayOffset') IS NULL
BEGIN
    ALTER TABLE dbo.TrainStops
        ADD DayOffset int NOT NULL CONSTRAINT DF_TrainStops_DayOffset DEFAULT 0;
END
GO

IF OBJECT_ID('dbo.StationAliases', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.StationAliases
    (
        Id        int IDENTITY(1,1) NOT NULL,
        Alias     nvarchar(200) NOT NULL,
        AliasKey  nvarchar(200) NOT NULL,
        StationId int NOT NULL,
        CONSTRAINT PK_StationAliases PRIMARY KEY (Id),
        CONSTRAINT FK_StationAliases_Stations_StationId
            FOREIGN KEY (StationId) REFERENCES dbo.Stations (Id) ON DELETE CASCADE
    );

    CREATE UNIQUE INDEX IX_StationAliases_AliasKey ON dbo.StationAliases (AliasKey);
END
GO
