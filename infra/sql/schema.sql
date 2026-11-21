-- Schema for the AppointmentsApi sample (Azure SQL / SQL Server).
-- Safe to run repeatedly: every statement checks whether its object already exists.
-- The app runs this on startup when APPOINTMENTS_CONNECTION_STRING is set.

IF OBJECT_ID(N'dbo.Doctors', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Doctors
    (
        Id        INT           NOT NULL PRIMARY KEY,
        Name      NVARCHAR(100) NOT NULL,
        Specialty NVARCHAR(50)  NOT NULL
    );
END;

IF OBJECT_ID(N'dbo.Appointments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Appointments
    (
        Id          UNIQUEIDENTIFIER  NOT NULL PRIMARY KEY,
        DoctorId    INT               NOT NULL REFERENCES dbo.Doctors (Id),
        PatientName NVARCHAR(100)     NOT NULL,
        StartsAt    DATETIMEOFFSET(0) NOT NULL,
        CreatedAt   DATETIMEOFFSET(3) NOT NULL
    );
END;

-- The database-level guarantee against double booking: one appointment per doctor per slot.
-- DATETIMEOFFSET compares by UTC instant, so 10:00+04:00 and 06:00+00:00 count as the same slot.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Appointments_Doctor_Slot')
BEGIN
    CREATE UNIQUE INDEX UX_Appointments_Doctor_Slot ON dbo.Appointments (DoctorId, StartsAt);
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Doctors)
BEGIN
    INSERT INTO dbo.Doctors (Id, Name, Specialty) VALUES
        (1, N'Dr. Nino Beridze',     N'cardiology'),
        (2, N'Dr. Giorgi Kapanadze', N'dermatology'),
        (3, N'Dr. Mariam Lomidze',   N'pediatrics'),
        (4, N'Dr. Levan Tsiklauri',  N'cardiology');
END;
