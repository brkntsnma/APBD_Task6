using System.Data;
using APBD_Task6.DTOs;
using Microsoft.Data.SqlClient;

namespace APBD_Task6.Services;

public class AppointmentService(IConfiguration configuration)
{
    private readonly string _connectionString =
        configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing 'DefaultConnection' in appsettings.json.");

    public async Task<List<AppointmentListDto>> GetAppointmentsAsync(string? status, string? patientLastName)
    {
        var result = new List<AppointmentListDto>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("""
            SELECT
                a.IdAppointment,
                a.AppointmentDate,
                a.Status,
                a.Reason,
                p.FirstName + N' ' + p.LastName AS PatientFullName,
                p.Email AS PatientEmail
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
            ORDER BY a.AppointmentDate;
            """, connection);

        command.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value =
            status is null ? DBNull.Value : status;
        command.Parameters.Add("@PatientLastName", SqlDbType.NVarChar, 80).Value =
            patientLastName is null ? DBNull.Value : patientLastName;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new AppointmentListDto
            {
                IdAppointment   = reader.GetInt32(0),
                AppointmentDate = reader.GetDateTime(1),
                Status          = reader.GetString(2),
                Reason          = reader.GetString(3),
                PatientFullName = reader.GetString(4),
                PatientEmail    = reader.GetString(5)
            });
        }

        return result;
    }

    public async Task<AppointmentDetailsDto?> GetAppointmentByIdAsync(int id)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("""
            SELECT
                a.IdAppointment,
                a.AppointmentDate,
                a.Status,
                a.Reason,
                a.InternalNotes,
                a.CreatedAt,
                p.FirstName + N' ' + p.LastName AS PatientFullName,
                p.Email AS PatientEmail,
                p.PhoneNumber AS PatientPhone,
                d.FirstName + N' ' + d.LastName AS DoctorFullName,
                d.LicenseNumber,
                s.Name AS Specialization
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            JOIN dbo.Doctors d ON d.IdDoctor = a.IdDoctor
            JOIN dbo.Specializations s ON s.IdSpecialization = d.IdSpecialization
            WHERE a.IdAppointment = @IdAppointment;
            """, connection);

        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = id;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new AppointmentDetailsDto
        {
            IdAppointment       = reader.GetInt32(0),
            AppointmentDate     = reader.GetDateTime(1),
            Status              = reader.GetString(2),
            Reason              = reader.GetString(3),
            InternalNotes       = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt           = reader.GetDateTime(5),
            PatientFullName     = reader.GetString(6),
            PatientEmail        = reader.GetString(7),
            PatientPhone        = reader.GetString(8),
            DoctorFullName      = reader.GetString(9),
            DoctorLicenseNumber = reader.GetString(10),
            Specialization      = reader.GetString(11)
        };
    }

    public async Task<(int? newId, string? error, int statusCode)> CreateAppointmentAsync(CreateAppointmentRequestDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Reason) || dto.Reason.Length > 250)
            return (null, "Reason is required and must be at most 250 characters.", 400);

        if (dto.AppointmentDate <= DateTime.UtcNow)
            return (null, "Appointment date cannot be in the past.", 400);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var patientCmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.Patients WHERE IdPatient = @Id AND IsActive = 1;", connection);
        patientCmd.Parameters.Add("@Id", SqlDbType.Int).Value = dto.IdPatient;
        if ((int)(await patientCmd.ExecuteScalarAsync())! == 0)
            return (null, "Patient does not exist or is not active.", 404);

        await using var doctorCmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.Doctors WHERE IdDoctor = @Id AND IsActive = 1;", connection);
        doctorCmd.Parameters.Add("@Id", SqlDbType.Int).Value = dto.IdDoctor;
        if ((int)(await doctorCmd.ExecuteScalarAsync())! == 0)
            return (null, "Doctor does not exist or is not active.", 404);

        await using var conflictCmd = new SqlCommand("""
            SELECT COUNT(1) FROM dbo.Appointments
            WHERE IdDoctor = @IdDoctor
              AND AppointmentDate = @AppointmentDate
              AND Status = N'Scheduled';
            """, connection);
        conflictCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = dto.IdDoctor;
        conflictCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = dto.AppointmentDate;
        if ((int)(await conflictCmd.ExecuteScalarAsync())! > 0)
            return (null, "Doctor already has a scheduled appointment at that time.", 409);

        await using var insertCmd = new SqlCommand("""
            INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
            OUTPUT INSERTED.IdAppointment
            VALUES (@IdPatient, @IdDoctor, @AppointmentDate, N'Scheduled', @Reason);
            """, connection);
        insertCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = dto.IdPatient;
        insertCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = dto.IdDoctor;
        insertCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = dto.AppointmentDate;
        insertCmd.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = dto.Reason;

        var newId = (int)(await insertCmd.ExecuteScalarAsync())!;
        return (newId, null, 201);
    }

    public async Task<(bool found, string? error, int statusCode)> UpdateAppointmentAsync(int id, UpdateAppointmentRequestDto dto)
    {
        string[] validStatuses = ["Scheduled", "Completed", "Cancelled"];
        if (!validStatuses.Contains(dto.Status))
            return (true, "Status must be Scheduled, Completed, or Cancelled.", 400);

        if (string.IsNullOrWhiteSpace(dto.Reason) || dto.Reason.Length > 250)
            return (true, "Reason is required and must be at most 250 characters.", 400);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var currentCmd = new SqlCommand(
            "SELECT Status, AppointmentDate FROM dbo.Appointments WHERE IdAppointment = @Id;", connection);
        currentCmd.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        await using var reader = await currentCmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (false, null, 404);

        var currentStatus = reader.GetString(0);
        var currentDate   = reader.GetDateTime(1);
        await reader.CloseAsync();

        if (currentStatus == "Completed" && dto.AppointmentDate != currentDate)
            return (true, "Cannot change date of a completed appointment.", 409);

        await using var patientCmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.Patients WHERE IdPatient = @Id AND IsActive = 1;", connection);
        patientCmd.Parameters.Add("@Id", SqlDbType.Int).Value = dto.IdPatient;
        if ((int)(await patientCmd.ExecuteScalarAsync())! == 0)
            return (true, "Patient does not exist or is not active.", 404);

        await using var doctorCmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.Doctors WHERE IdDoctor = @Id AND IsActive = 1;", connection);
        doctorCmd.Parameters.Add("@Id", SqlDbType.Int).Value = dto.IdDoctor;
        if ((int)(await doctorCmd.ExecuteScalarAsync())! == 0)
            return (true, "Doctor does not exist or is not active.", 404);

        if (dto.AppointmentDate != currentDate)
        {
            await using var conflictCmd = new SqlCommand("""
                SELECT COUNT(1) FROM dbo.Appointments
                WHERE IdDoctor = @IdDoctor
                  AND AppointmentDate = @AppointmentDate
                  AND Status = N'Scheduled'
                  AND IdAppointment <> @IdAppointment;
                """, connection);
            conflictCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = dto.IdDoctor;
            conflictCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = dto.AppointmentDate;
            conflictCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = id;
            if ((int)(await conflictCmd.ExecuteScalarAsync())! > 0)
                return (true, "Doctor already has a scheduled appointment at that time.", 409);
        }

        await using var updateCmd = new SqlCommand("""
            UPDATE dbo.Appointments
            SET IdPatient       = @IdPatient,
                IdDoctor        = @IdDoctor,
                AppointmentDate = @AppointmentDate,
                Status          = @Status,
                Reason          = @Reason,
                InternalNotes   = @InternalNotes
            WHERE IdAppointment = @IdAppointment;
            """, connection);
        updateCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = dto.IdPatient;
        updateCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = dto.IdDoctor;
        updateCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = dto.AppointmentDate;
        updateCmd.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = dto.Status;
        updateCmd.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = dto.Reason;
        updateCmd.Parameters.Add("@InternalNotes", SqlDbType.NVarChar, 500).Value =
            dto.InternalNotes is null ? DBNull.Value : dto.InternalNotes;
        updateCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = id;

        await updateCmd.ExecuteNonQueryAsync();
        return (true, null, 200);
    }

    public async Task<(bool found, string? error, int statusCode)> DeleteAppointmentAsync(int id)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var selectCmd = new SqlCommand(
            "SELECT Status FROM dbo.Appointments WHERE IdAppointment = @Id;", connection);
        selectCmd.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        var status = await selectCmd.ExecuteScalarAsync() as string;

        if (status is null) return (false, null, 404);
        if (status == "Completed") return (true, "Cannot delete a completed appointment.", 409);

        await using var deleteCmd = new SqlCommand(
            "DELETE FROM dbo.Appointments WHERE IdAppointment = @Id;", connection);
        deleteCmd.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        await deleteCmd.ExecuteNonQueryAsync();

        return (true, null, 204);
    }
}