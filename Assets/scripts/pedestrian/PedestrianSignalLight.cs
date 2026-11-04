// ============================================================
// PedestrianSignalLight.cs
// Semaforo peatonal SOLO VISUAL: muestra la fase peatonal del paso
// (IntersectionController.GetPedestrianState(arm)): rojo fijo = no cruzar,
// verde fijo = cruzar, verde intermitente (2 Hz) = despeje.
// Lo construye SimBici > Build Pedestrian Setup (poste, caja y dos lentes con halo).
// La parte visual va en Update; la fase la decide el controlador en FixedUpdate.
// ============================================================

using UnityEngine;

public class PedestrianSignalLight : MonoBehaviour
{
    [SerializeField] private Crosswalk crosswalk;
    [SerializeField] private Renderer redLens;
    [SerializeField] private Renderer greenLens;
    [SerializeField] private Renderer redHalo;
    [SerializeField] private Renderer greenHalo;
    [SerializeField] private Material redOn, greenOn, lensOff;
    [SerializeField] private float flashHz = 2f;

    public void Configure(Crosswalk cw, Renderer red, Renderer green, Renderer rHalo, Renderer gHalo,
                          Material rOn, Material gOn, Material off)
    {
        crosswalk = cw; redLens = red; greenLens = green; redHalo = rHalo; greenHalo = gHalo;
        redOn = rOn; greenOn = gOn; lensOff = off;
    }

    private void Update()
    {
        if (crosswalk == null) return;
        PedestrianPhase phase = crosswalk.PedState.Phase;
        bool red = phase == PedestrianPhase.DontWalk;
        bool green = phase == PedestrianPhase.Walk ||
                     (phase == PedestrianPhase.FlashingDontWalk && Mathf.Repeat(Time.time * flashHz, 1f) < 0.5f);

        if (redLens != null) redLens.sharedMaterial = red ? redOn : lensOff;
        if (greenLens != null) greenLens.sharedMaterial = green ? greenOn : lensOff;
        if (redHalo != null) redHalo.enabled = red;
        if (greenHalo != null) greenHalo.enabled = green;
    }
}
