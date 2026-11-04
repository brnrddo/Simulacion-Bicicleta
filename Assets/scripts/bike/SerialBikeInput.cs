// SerialBikeInput.cs
// Lee el CSV crudo del Arduino en un hilo secundario.
// Responsabilidad unica: I/O serial + parseo. Cero logica de dominio.
// Formato esperado: pulse_count,pulse_timestamp_us,now_us,pot_raw

using System;
using System.IO.Ports;
using System.Threading;
using UnityEngine;

[DefaultExecutionOrder(-200)]  // abre el puerto antes que nadie lo consulte
public class SerialBikeInput : MonoBehaviour
{
    [Header("Configuracion del puerto")]
    [SerializeField] private string portName = "COM3";
    [SerializeField] private int baudRate = 115200;
    [SerializeField] private int readTimeoutMs = 100;

    [Tooltip("Si esta activo, abrir el puerto resetea el Arduino Uno (~1.6 s sin datos). " +
             "Desactivado, el sketch sigue corriendo y los datos llegan de inmediato.")]
    [SerializeField] private bool assertDtrOnOpen = false;

    [Tooltip("Si el buffer de entrada acumula mas bytes que esto, se descarta. " +
             "Sin esto, un hitch largo de Unity deja un backlog que se traduce en " +
             "latencia permanente durante el resto de la sesion.")]
    [SerializeField] private int maxBacklogBytes = 512;

    [Header("Debug")]
    [SerializeField] private bool logConnectionStatus = true;

    [Header("Diagnostico (desactivar en sesiones)")]
    [SerializeField] private bool logRawLines = false;

    private int rejectedLines = 0;

    // struct (no class): copia por valor -> sin referencias compartidas
    // entre el hilo de lectura y el hilo principal.
    public struct RawSensorData
    {
        public uint pulseCount;        // contador acumulado de pulsos Hall
        public uint pulseTimestampUs;  // micros() del ultimo pulso
        public uint nowUs;             // micros() al momento de armar el paquete
        public int potRaw;             // ADC crudo, 0-1023
        public bool isValid;
    }

    private SerialPort serialPort;
    private Thread readThread;
    private volatile bool isRunning;

    private readonly object dataLock = new object();
    private RawSensorData latestData;
    private int readSequence;

    /// <summary>
    /// Contador monotono de lineas validas parseadas. Se expone como
    /// propiedad de solo lectura en vez del viejo HasNewData(): esa era
    /// una consulta con efecto secundario (consumia el flag), asi que
    /// si dos consumidores la llamaban -- por ejemplo el processor y el
    /// logger de la Fase 5 -- uno de los dos perdia lecturas de forma
    /// intermitente y silenciosa. Ahora cada consumidor guarda su propia
    /// copia del ultimo valor visto.
    /// </summary>
    public int ReadSequence
    {
        get { lock (dataLock) { return readSequence; } }
    }

    public bool IsConnected { get; private set; }

    private void Start() => OpenSerialPort();

    private void OpenSerialPort()
    {
        try
        {
            serialPort = new SerialPort(portName, baudRate)
            {
                ReadTimeout = readTimeoutMs,
                NewLine = "\n",
                DtrEnable = assertDtrOnOpen,
                ReadBufferSize = 4096
            };

            serialPort.Open();
            serialPort.DiscardInBuffer();  // descarta basura del arranque
            IsConnected = true;

            isRunning = true;
            readThread = new Thread(ReadSerialLoop) { IsBackground = true };
            readThread.Start();

            if (logConnectionStatus)
                Debug.Log($"[SerialBikeInput] Conectado en {portName} @ {baudRate} baud.");
        }
        catch (Exception e)
        {
            IsConnected = false;
            Debug.LogError($"[SerialBikeInput] No se pudo abrir {portName}: {e.Message}. " +
                           "Verificar que el Monitor Serial de Arduino IDE este cerrado.");
        }
    }

    // Hilo secundario. Nunca tocar UnityEngine API aca salvo Debug.*.
    private void ReadSerialLoop()
    {
        while (isRunning)
        {
            try
            {
                // El paquete mas nuevo siempre gana. No se pierde informacion:
                // pulseCount es acumulativo, asi que descartar lineas
                // intermedias no pierde pulsos, solo resolucion temporal.
                if (serialPort.BytesToRead > maxBacklogBytes)
                    serialPort.DiscardInBuffer();

                string line = serialPort.ReadLine();

                if (TryParseLine(line, out RawSensorData parsed))
                {
                    lock (dataLock) { latestData = parsed; readSequence++; }
                    if (logRawLines && readSequence % 90 == 0)
                        Debug.Log($"[Serial] OK #{readSequence}: '{line.Trim()}'");
                }
                else
                {
                 rejectedLines++;
                if (logRawLines && rejectedLines % 90 == 1)
                    Debug.LogWarning($"[Serial] Rechazadas {rejectedLines}. Ultima: '{line.Trim()}'");
                }
            }
            catch (TimeoutException)
            {
                // Esperado si no llega dato dentro de readTimeoutMs.
            }
            catch (Exception e)
            {
                if (isRunning)  // evita ruido de log al cerrar
                    Debug.LogWarning($"[SerialBikeInput] Error de lectura: {e.Message}");
            }
        }
    }

    private bool TryParseLine(string line, out RawSensorData data)
    {
        data = default;

        if (string.IsNullOrEmpty(line)) return false;

        string[] parts = line.Split(',');
        if (parts.Length != 4) return false;  // linea cortada o corrupta

        if (!uint.TryParse(parts[0], out uint pulseCount)) return false;
        if (!uint.TryParse(parts[1], out uint pulseTsUs)) return false;
        if (!uint.TryParse(parts[2], out uint nowUs)) return false;
        if (!int.TryParse(parts[3].Trim(), out int potRaw)) return false;

        data.pulseCount = pulseCount;
        data.pulseTimestampUs = pulseTsUs;
        data.nowUs = nowUs;
        data.potRaw = potRaw;
        data.isValid = true;
        return true;
    }

    /// <summary>Ultima lectura cruda disponible. Thread-safe.</summary>
    public RawSensorData GetLatestData()
    {
        lock (dataLock) { return latestData; }
    }

    private void OnDestroy()
    {
        isRunning = false;

        if (readThread != null && readThread.IsAlive)
            readThread.Join(readTimeoutMs + 200);

        if (serialPort != null && serialPort.IsOpen)
            serialPort.Close();

        IsConnected = false;
    }
}
