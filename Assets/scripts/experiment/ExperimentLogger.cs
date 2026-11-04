// ============================================================
// ExperimentLogger.cs
// Escribe tres archivos por sesion en Documentos/SimBici/Logs:
//   <participante>_<condicion>_<fecha>_meta.txt        parametros de la sesion
//   <participante>_<condicion>_<fecha>_continuous.csv  estado del ciclista a 90 Hz
//   <participante>_<condicion>_<fecha>_events.csv      todo lo que pase por EventBus
// Punto decimal SIEMPRE con InvariantCulture: con configuracion regional
// chilena Windows escribe "3,14" y el CSV queda con columnas corridas.
// ============================================================

using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

[DefaultExecutionOrder(100)]  // despues de BicycleController (0) y RiderRoute (50)
public class ExperimentLogger : MonoBehaviour
{
    [Header("Identificacion de la sesion")]
    [SerializeField] private string participantId = "P000";
    [SerializeField] private string conditionId = "sandbox";

    [Header("Referencias")]
    [SerializeField] private BicycleController bike;
    [Tooltip("Opcional. Sin ruta, las columnas de ruta quedan vacias.")]
    [SerializeField] private RiderRoute route;

    [Header("Salida")]
    [Tooltip("Vacio = Documentos/SimBici/Logs")]
    [SerializeField] private string outputFolder = "";
    [SerializeField] private bool logContinuous = true;
    [SerializeField] private float flushIntervalSec = 1f;

    public string SessionBasePath { get; private set; }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly char[] CsvSpecialChars = { ',', '"', '\n', '\r' };
    private StreamWriter continuousWriter;
    private StreamWriter eventsWriter;
    private readonly StringBuilder sb = new StringBuilder(256);
    private float nextFlushTime;
    private long tick;
    private bool closed;

    private void Awake()
    {
        if (bike == null)
        {
            Debug.LogError("[ExperimentLogger] Falta asignar BicycleController.");
            enabled = false;
            return;
        }

        SessionClock.Start();

        string folder = string.IsNullOrWhiteSpace(outputFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SimBici", "Logs")
            : outputFolder;
        Directory.CreateDirectory(folder);

        string stamp = SessionClock.StartUtc.ToLocalTime().ToString("yyyyMMdd_HHmmss", Inv);
        SessionBasePath = Path.Combine(folder, $"{Sanitize(participantId)}_{Sanitize(conditionId)}_{stamp}");

        WriteMeta();

        var utf8 = new UTF8Encoding(false);  // sin BOM: pandas y Excel lo leen igual
        eventsWriter = new StreamWriter(SessionBasePath + "_events.csv", false, utf8);
        eventsWriter.WriteLine("t_session,t_unity,type,source_id,detail,rider_x,rider_z,route_dist_m,lateral_offset_m,route_path");

        if (logContinuous)
        {
            continuousWriter = new StreamWriter(SessionBasePath + "_continuous.csv", false, utf8);
            continuousWriter.WriteLine("t_session,t_unity,tick,pos_x,pos_y,pos_z,yaw_deg,speed_mps," +
                                       "steer_phys_deg,steer_eff_deg,yaw_rate_dps," +
                                       "route_dist_m,lateral_offset_m,off_route,input_source,route_path");
        }

        EventBus.OnEvent += HandleEvent;
        Debug.Log($"[ExperimentLogger] Sesion iniciada: {SessionBasePath}");
    }

    private void Start()
    {
        EventBus.Publish("session_start", "logger", $"participant={participantId};condition={conditionId}");
    }

    private void FixedUpdate()
    {
        if (closed) return;

        if (continuousWriter != null)
        {
            Vector3 p = bike.RiderPosition;
            bool hasRoute = route != null && route.HasFix;

            sb.Clear();
            Append(SessionClock.Now, "F4"); sb.Append(',');
            Append(Time.timeAsDouble, "F4"); sb.Append(',');
            sb.Append(tick.ToString(Inv)); sb.Append(',');
            Append(p.x, "F3"); sb.Append(',');
            Append(p.y, "F3"); sb.Append(',');
            Append(p.z, "F3"); sb.Append(',');
            Append(bike.transform.eulerAngles.y, "F2"); sb.Append(',');
            Append(bike.CurrentSpeedMps, "F3"); sb.Append(',');
            Append(bike.PhysicalSteeringDeg, "F2"); sb.Append(',');
            Append(bike.EffectiveSteeringDeg, "F2"); sb.Append(',');
            Append(bike.YawRateDegPerSec, "F2"); sb.Append(',');
            // Sin ruta la celda queda vacia, pero la coma se escribe igual
            if (hasRoute) Append(route.DistanceAlongM, "F3");
            sb.Append(',');
            if (hasRoute) Append(route.LateralOffsetM, "F3");
            sb.Append(',');
            if (hasRoute) sb.Append(route.IsOffRoute ? '1' : '0');
            sb.Append(',');
            sb.Append(bike.ActiveSourceName);
            sb.Append(',');
            if (hasRoute) sb.Append(route.CurrentPath.name);

            continuousWriter.WriteLine(sb.ToString());
        }

        tick++;

        if (Time.unscaledTime >= nextFlushTime)
        {
            continuousWriter?.Flush();
            eventsWriter?.Flush();
            nextFlushTime = Time.unscaledTime + flushIntervalSec;
        }
    }

    private void HandleEvent(SimEvent e)
    {
        if (closed || eventsWriter == null) return;

        // Al cerrar la escena la bici puede destruirse antes que el logger:
        // "bike != null" usa el null de Unity, que detecta objetos ya destruidos.
        Vector3 p = bike != null ? bike.RiderPosition : Vector3.zero;
        bool hasRoute = route != null && route.HasFix;

        sb.Clear();
        Append(e.TimeSession, "F4"); sb.Append(',');
        Append(e.TimeUnity, "F4"); sb.Append(',');
        sb.Append(Csv(e.Type)); sb.Append(',');
        sb.Append(Csv(e.SourceId)); sb.Append(',');
        sb.Append(Csv(e.Detail)); sb.Append(',');
        Append(p.x, "F3"); sb.Append(',');
        Append(p.z, "F3"); sb.Append(',');
        if (hasRoute) Append(route.DistanceAlongM, "F3");
        sb.Append(',');
        if (hasRoute) Append(route.LateralOffsetM, "F3");
        sb.Append(',');
        if (hasRoute) sb.Append(route.CurrentPath.name);

        eventsWriter.WriteLine(sb.ToString());
    }

    private void WriteMeta()
    {
        var lines = new[]
        {
            $"participant={participantId}",
            $"condition={conditionId}",
            $"scene={SceneManager.GetActiveScene().name}",
            $"start_utc={SessionClock.StartUtc.ToString("o", Inv)}",
            $"unity_version={Application.unityVersion}",
            $"app_version={Application.version}",
            $"fixed_dt={Time.fixedDeltaTime.ToString("F5", Inv)}",
            $"cyclist_network_length_m={(route != null ? route.LengthM.ToString("F2", Inv) : "")}",
        };
        File.WriteAllLines(SessionBasePath + "_meta.txt", lines);
    }

    // ------------------------------------------------------------
    // Cierre: se llama desde OnApplicationQuit y OnDestroy; solo actua una vez.
    // ------------------------------------------------------------
    private void CloseSession()
    {
        if (closed) return;

        EventBus.Publish("session_end", "logger", $"ticks={tick}");
        EventBus.OnEvent -= HandleEvent;
        closed = true;

        continuousWriter?.Flush(); continuousWriter?.Dispose(); continuousWriter = null;
        eventsWriter?.Flush(); eventsWriter?.Dispose(); eventsWriter = null;
        Debug.Log("[ExperimentLogger] Sesion cerrada.");
    }

    private void OnApplicationQuit() => CloseSession();
    private void OnDestroy() => CloseSession();

    // ------------------------------------------------------------
    // Helpers de formato
    // ------------------------------------------------------------
    private void Append(double v, string format) => sb.Append(v.ToString(format, Inv));
    private void Append(float v, string format) => sb.Append(v.ToString(format, Inv));

    /// <summary>Escapa un campo de texto para CSV (comillas si trae coma, comilla o salto de linea).</summary>
    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(CsvSpecialChars) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
