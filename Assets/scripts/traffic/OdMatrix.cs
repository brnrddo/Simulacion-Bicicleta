// ============================================================
// OdMatrix.cs
// Matriz origen-destino del trafico (veh/h), tal como se entrega antes de
// empezar: una lista de pares con su media y su desviacion estandar.
//
//   { "pairs": [ { "from": "C2", "to": "C3", "meanVehPerHour": 500, "sdVehPerHour": 50 }, ... ] }
//
// En cada repeticion cada celda se sortea: q = max(0, N(media, sd)). Los pares
// que no aparecen valen 0. Logica pura (sin MonoBehaviour) para testearla; la
// usa OdDemand.
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

[Serializable]
public class OdPair
{
    public string from;
    public string to;
    public float meanVehPerHour;
    public float sdVehPerHour;
}

[Serializable]
public class OdMatrixFile
{
    public List<OdPair> pairs = new List<OdPair>();
}

public static class OdMatrix
{
    /// <summary>Lee el JSON. Error = null si esta bien.</summary>
    public static OdMatrixFile Parse(string json, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json)) { error = "archivo vacio"; return null; }
        OdMatrixFile file;
        try { file = JsonUtility.FromJson<OdMatrixFile>(json); }
        catch (Exception e) { error = "JSON invalido: " + e.Message; return null; }
        if (file == null || file.pairs == null || file.pairs.Count == 0) { error = "no tiene pares (\"pairs\")"; return null; }
        return file;
    }

    /// <summary>
    /// Revisa que cada par vaya de un nodo de entrada a uno de salida, sin pares repetidos y con numeros validos.
    /// Error = null si esta bien.
    /// </summary>
    public static bool Validate(OdMatrixFile file, ICollection<string> entryIds, ICollection<string> exitIds, out string error)
    {
        error = null;
        var seen = new HashSet<string>();
        foreach (var p in file.pairs)
        {
            string key = $"{p.from}->{p.to}";
            if (!entryIds.Contains(p.from)) error = $"{key}: {p.from} no es un nodo de entrada ({string.Join(", ", entryIds)})";
            else if (!exitIds.Contains(p.to)) error = $"{key}: {p.to} no es un nodo de salida ({string.Join(", ", exitIds)})";
            else if (!seen.Add(key)) error = $"{key}: par repetido";
            else if (float.IsNaN(p.meanVehPerHour) || p.meanVehPerHour < 0f) error = $"{key}: media invalida ({p.meanVehPerHour})";
            else if (float.IsNaN(p.sdVehPerHour) || p.sdVehPerHour < 0f) error = $"{key}: desviacion estandar invalida ({p.sdVehPerHour})";
            if (error != null) return false;
        }
        return true;
    }

    /// <summary>
    /// Sorteo de una repeticion: q = max(0, N(media, sd)) por par, en el orden del archivo (determinista).
    /// Un par con media 0 queda en 0 (no consume numeros distintos: siempre se sortean dos por par).
    /// </summary>
    public static float[] Sample(OdMatrixFile file, System.Random rng)
    {
        var q = new float[file.pairs.Count];
        for (int i = 0; i < q.Length; i++)
        {
            var p = file.pairs[i];
            double z = StandardNormal(rng);
            q[i] = p.meanVehPerHour <= 0f ? 0f : Mathf.Max(0f, (float)(p.meanVehPerHour + p.sdVehPerHour * z));
        }
        return q;
    }

    /// <summary>Normal estandar por Box-Muller (consume siempre dos numeros del generador).</summary>
    public static double StandardNormal(System.Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();   // (0, 1]
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Indice al azar con probabilidad proporcional al peso (un numero del generador). -1 si todos son 0.</summary>
    public static int PickIndex(System.Random rng, IReadOnlyList<float> weights)
    {
        double total = 0.0;
        for (int i = 0; i < weights.Count; i++) total += Math.Max(0f, weights[i]);
        if (total <= 0.0) return -1;
        double r = rng.NextDouble() * total;
        int last = -1;
        for (int i = 0; i < weights.Count; i++)
        {
            if (weights[i] <= 0f) continue;
            last = i;
            r -= weights[i];
            if (r < 0.0) return i;
        }
        return last;
    }

    /// <summary>Resumen de una linea por par, para el log: "C2->C3 media 500 sd 50 sorteado 487.3".</summary>
    public static string Describe(OdMatrixFile file, IReadOnlyList<float> sampled)
    {
        var inv = CultureInfo.InvariantCulture;
        return string.Join("; ", file.pairs.Select((p, i) => string.Format(inv, "{0}->{1} media {2:F0} sd {3:F0} sorteado {4:F1}",
            p.from, p.to, p.meanVehPerHour, p.sdVehPerHour, sampled[i])));
    }
}
