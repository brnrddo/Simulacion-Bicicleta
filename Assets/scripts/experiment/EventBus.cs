// ============================================================
// EventBus.cs
// Canal unico por el que pasa todo evento experimental
// (adelantamientos, peatones, choques, reinicios, fin de ruta...).
// Quien genera el evento no sabe quien lo escucha: el logger, el
// manejador de choques o un futuro puente a LSL se suscriben sin
// tocar el codigo de los agentes.
// ============================================================

using System;
using UnityEngine;

/// <summary>Un evento con doble timestamp (reloj real + tiempo de Unity).</summary>
public readonly struct SimEvent
{
    public readonly double TimeSession;  // s de reloj real (SessionClock)
    public readonly double TimeUnity;    // s de Time.timeAsDouble
    public readonly string Type;         // p. ej. "collision", "overtake_pass"
    public readonly string SourceId;     // id del agente u objeto que lo genero
    public readonly string Detail;       // parametros extra "clave=valor;clave=valor"

    public SimEvent(double timeSession, double timeUnity, string type, string sourceId, string detail)
    {
        TimeSession = timeSession;
        TimeUnity = timeUnity;
        Type = type;
        SourceId = sourceId;
        Detail = detail;
    }
}

public static class EventBus
{
    /// <summary>Suscribirse con EventBus.OnEvent += MiMetodo; desuscribirse con -=.</summary>
    public static event Action<SimEvent> OnEvent;

    /// <summary>
    /// Publica un evento. Llamar SOLO desde el hilo principal de Unity
    /// (Update/FixedUpdate/callbacks de fisica), nunca desde el hilo del serial.
    /// </summary>
    public static void Publish(string type, string sourceId = "", string detail = "")
    {
        // Si alguien publica antes de que el logger arranque el reloj, arrancarlo aqui
        // para no escribir tiempos basura.
        if (!SessionClock.IsRunning) SessionClock.Start();

        var e = new SimEvent(SessionClock.Now, Time.timeAsDouble, type, sourceId, detail);
        OnEvent?.Invoke(e);
    }

    // Con "Enter Play Mode Options" (sin domain reload) los eventos static
    // sobreviven entre Plays y se acumulan suscriptores de sesiones anteriores.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        OnEvent = null;
    }
}
