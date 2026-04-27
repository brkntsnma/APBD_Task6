using System.Data;
using APBD_Task6.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace APBD_Task6.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AppointmentsController : ControllerBase
{
    private readonly string _connectionString;

    public AppointmentsController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Missing 'DefaultConnection' in appsettings.json.");
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<AppointmentListDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAppointments(
        [FromQuery] string? status,
        [FromQuery] string? patientLastName)
    {
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
            string.IsNullOrWhiteSpace(status) ? DBNull.Value : status;
        command.Parameters.Add("@PatientLastName", SqlDbType.NVarChar, 80).Value =
            string.IsNullOrWhiteSpace(patientLastName) ? DBNull.Value : patientLastName;

        await using var reader = await command.ExecuteReaderAsync();
        var results = new List<AppointmentListDto>();

        while (await reader.ReadAsync())
        {
            results.Add(new AppointmentListDto
            {
                IdAppointment   = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
                AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
                Status          = reader.GetString(reader.GetOrdinal("Status")),
                Reason          = reader.GetString(reader.GetOrdinal("Reason")),
                PatientFullName = reader.GetString(reader.GetOrdinal("PatientFullName")),
                PatientEmail    = reader.GetString(reader.GetOrdinal("PatientEmail"))
            });
        }

        return Ok(results);
    }

    [HttpGet("{idAppointment:int}", Name = "GetAppointment")]
    [ProducesResponseType(typeof(AppointmentDetailsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAppointment(int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var dto = await FetchAppointmentDetails(connection, idAppointment);
        if (dto is null)
            return NotFound(new ErrorResponseDto($"Appointment with id {idAppointment} was not found."));

        return Ok(dto);
    }

    [HttpPost]
    [ProducesResponseType(typeof(AppointmentDetailsDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentRequestDto request)
    {
        if (request.AppointmentDate <= DateTime.UtcNow)
            return BadRequest(new ErrorResponseDto("Appointment date cannot be in the past."));

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 250)
            return BadRequest(new ErrorResponseDto("Reason is required and must be at most 250 characters."));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        if (!await IsPatientActive(connection, request.IdPatient))
            return NotFound(new ErrorResponseDto($"Patient with id {request.IdPatient} does not exist or is inactive."));

        if (!await IsDoctorActive(connection, request.IdDoctor))
            return NotFound(new ErrorResponseDto($"Doctor with id {request.IdDoctor} does not exist or is inactive."));

        if (await HasDoctorConflict(connection, request.IdDoctor, request.AppointmentDate))
            return Conflict(new ErrorResponseDto("The doctor already has a scheduled appointment at this time."));

        await using var insertCmd = new SqlCommand("""
            INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason, InternalNotes)
            OUTPUT INSERTED.IdAppointment
            VALUES (@IdPatient, @IdDoctor, @AppointmentDate, N'Scheduled', @Reason, NULL);
            """, connection);

        insertCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        insertCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        insertCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        insertCmd.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason.Trim();

        var newId = (int)(await insertCmd.ExecuteScalarAsync()
                          ?? throw new InvalidOperationException("Could not create appointment."));

        var details = await FetchAppointmentDetails(connection, newId);
        return CreatedAtRoute("GetAppointment", new { idAppointment = newId }, details);
    }

    [HttpPut("{idAppointment:int}")]
    [ProducesResponseType(typeof(AppointmentDetailsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateAppointment(int idAppointment, [FromBody] UpdateAppointmentRequestDto request)
    {
        if (!AllowedStatuses.Contains(request.Status))
            return BadRequest(new ErrorResponseDto("Status must be one of: Scheduled, Completed, Cancelled."));

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 250)
            return BadRequest(new ErrorResponseDto("Reason is required and must be at most 250 characters."));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var current = await FetchAppointmentCore(connection, idAppointment);
        if (current is null)
            return NotFound(new ErrorResponseDto($"Appointment with id {idAppointment} was not found."));

        if (current.Status == "Completed" && current.AppointmentDate != request.AppointmentDate)
            return Conflict(new ErrorResponseDto("Completed appointments cannot have their date changed."));

        if (!await IsPatientActive(connection, request.IdPatient))
            return NotFound(new ErrorResponseDto($"Patient with id {request.IdPatient} does not exist or is inactive."));

        if (!await IsDoctorActive(connection, request.IdDoctor))
            return NotFound(new ErrorResponseDto($"Doctor with id {request.IdDoctor} does not exist or is inactive."));

        var dateOrDoctorChanged = current.AppointmentDate != request.AppointmentDate
                                  || current.IdDoctor != request.IdDoctor;
        if (dateOrDoctorChanged && await HasDoctorConflict(connection, request.IdDoctor, request.AppointmentDate, idAppointment))
            return Conflict(new ErrorResponseDto("The doctor already has a scheduled appointment at this time."));

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

        updateCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        updateCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        updateCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        updateCmd.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = request.Status;
        updateCmd.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason.Trim();
        updateCmd.Parameters.Add("@InternalNotes", SqlDbType.NVarChar, 500).Value =
            string.IsNullOrWhiteSpace(request.InternalNotes) ? DBNull.Value : request.InternalNotes.Trim();
        updateCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await updateCmd.ExecuteNonQueryAsync();

        var updated = await FetchAppointmentDetails(connection, idAppointment);
        return Ok(updated);
    }

    [HttpDelete("{idAppointment:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteAppointment(int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var current = await FetchAppointmentCore(connection, idAppointment);
        if (current is null)
            return NotFound(new ErrorResponseDto($"Appointment with id {idAppointment} was not found."));

        if (current.Status == "Completed")
            return Conflict(new ErrorResponseDto("Completed appointments cannot be deleted."));

        await using var deleteCmd = new SqlCommand(
            "DELETE FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;", connection);
        deleteCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        await deleteCmd.ExecuteNonQueryAsync();

        return NoContent();
    }

    private static readonly HashSet<string> AllowedStatuses = ["Scheduled", "Completed", "Cancelled"];

    private static async Task<bool> IsPatientActive(SqlConnection connection, int idPatient)
    {
        await using var command = new SqlCommand("""
            SELECT COUNT(1) FROM dbo.Patients
            WHERE IdPatient = @IdPatient AND IsActive = 1;
            """, connection);
        command.Parameters.Add("@IdPatient", SqlDbType.Int).Value = idPatient;
        return (int)(await command.ExecuteScalarAsync() ?? 0) > 0;
    }

    private static async Task<bool> IsDoctorActive(SqlConnection connection, int idDoctor)
    {
        await using var command = new SqlCommand("""
            SELECT COUNT(1) FROM dbo.Doctors
            WHERE IdDoctor = @IdDoctor AND IsActive = 1;
            """, connection);
        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = idDoctor;
        return (int)(await command.ExecuteScalarAsync() ?? 0) > 0;
    }

    private static async Task<bool> HasDoctorConflict(
        SqlConnection connection,
        int idDoctor,
        DateTime appointmentDate,
        int? excludedId = null)
    {
        await using var command = new SqlCommand("""
            SELECT COUNT(1) FROM dbo.Appointments
            WHERE IdDoctor = @IdDoctor
              AND AppointmentDate = @AppointmentDate
              AND Status = N'Scheduled'
              AND (@ExcludedId IS NULL OR IdAppointment <> @ExcludedId);
            """, connection);
        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = idDoctor;
        command.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = appointmentDate;
        command.Parameters.Add("@ExcludedId", SqlDbType.Int).Value =
            excludedId.HasValue ? excludedId.Value : DBNull.Value;
        return (int)(await command.ExecuteScalarAsync() ?? 0) > 0;
    }

    private static async Task<AppointmentCoreData?> FetchAppointmentCore(SqlConnection connection, int idAppointment)
    {
        await using var command = new SqlCommand("""
            SELECT IdAppointment, IdDoctor, AppointmentDate, Status
            FROM dbo.Appointments
            WHERE IdAppointment = @IdAppointment;
            """, connection);
        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new AppointmentCoreData
        {
            IdAppointment   = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
            IdDoctor        = reader.GetInt32(reader.GetOrdinal("IdDoctor")),
            AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
            Status          = reader.GetString(reader.GetOrdinal("Status"))
        };
    }

    private static async Task<AppointmentDetailsDto?> FetchAppointmentDetails(SqlConnection connection, int idAppointment)
    {
        await using var command = new SqlCommand("""
            SELECT
                a.IdAppointment,
                a.IdPatient,
                a.IdDoctor,
                a.AppointmentDate,
                a.Status,
                a.Reason,
                a.InternalNotes,
                a.CreatedAt,
                p.FirstName + N' ' + p.LastName AS PatientFullName,
                p.Email AS PatientEmail,
                p.PhoneNumber AS PatientPhone,
                d.FirstName + N' ' + d.LastName AS DoctorFullName,
                d.LicenseNumber AS DoctorLicenseNumber,
                s.Name AS Specialization
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            JOIN dbo.Doctors d ON d.IdDoctor = a.IdDoctor
            JOIN dbo.Specializations s ON s.IdSpecialization = d.IdSpecialization
            WHERE a.IdAppointment = @IdAppointment;
            """, connection);
        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new AppointmentDetailsDto
        {
            IdAppointment       = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
            IdPatient           = reader.GetInt32(reader.GetOrdinal("IdPatient")),
            IdDoctor            = reader.GetInt32(reader.GetOrdinal("IdDoctor")),
            AppointmentDate     = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
            Status              = reader.GetString(reader.GetOrdinal("Status")),
            Reason              = reader.GetString(reader.GetOrdinal("Reason")),
            InternalNotes       = reader.IsDBNull(reader.GetOrdinal("InternalNotes")) ? null : reader.GetString(reader.GetOrdinal("InternalNotes")),
            CreatedAt           = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
            PatientFullName     = reader.GetString(reader.GetOrdinal("PatientFullName")),
            PatientEmail        = reader.GetString(reader.GetOrdinal("PatientEmail")),
            PatientPhone        = reader.GetString(reader.GetOrdinal("PatientPhone")),
            DoctorFullName      = reader.GetString(reader.GetOrdinal("DoctorFullName")),
            DoctorLicenseNumber = reader.GetString(reader.GetOrdinal("DoctorLicenseNumber")),
            Specialization      = reader.GetString(reader.GetOrdinal("Specialization"))
        };
    }

    private class AppointmentCoreData
    {
        public int IdAppointment { get; set; }
        public int IdDoctor { get; set; }
        public DateTime AppointmentDate { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}