using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgentsPoker.Core;

/// <summary>
/// CountMaster — agente de Limit Texas Hold'em (Fundamentos de IA, Universidad de La Sabana).
/// 1) Estima su equity con Monte Carlo contra los rivales activos, repartiéndole a cada uno manos coherentes
///    con su rango observado (VPIP en la mesa + si subió preflop + si apostó después del flop).
/// 2) Compara la equity con las pot odds para decidir FOLD / CALL / RAISE (nunca FOLD si pasar es gratis).
/// 3) Ajusta los umbrales según su posición respecto al top 3 de la mesa y las manos restantes.
/// Aprende de los rivales con IHandObserver.OnHandEnd; lo aprendido dura solo la mesa actual.
/// </summary>
public sealed class CountMaster : IPokerAgent, IHandObserver
{
    private const int PresupuestoMs = 150;          // muy por debajo de 1000 ms: la Arena (Unity/Mono) es más lenta
    private const int MaxSimulaciones = 20000;

    // --- Control de riesgo por posición (v1) ---
    private const int ManosPorMesa = 30;            // formato del torneo
    private const int Clasifican = 3;               // top 3 avanza
    private const double ColchonCompleto = 400;     // fichas de ventaja a partir de las cuales se protege al máximo
    private const double MargenSubida = 0.10;       // cuánto se mueve el umbral de subida con riesgo = ±1
    private const double MargenPago = 0.06;         // cuánto se mueve el umbral de pago con riesgo = ±1
    private readonly Random _rng = new Random(12345); // semilla fija = partidas reproducibles (opcional)

    /// <summary>Lo que sabemos de cada rival (por nombre: el asiento puede cambiar entre mesas).</summary>
    private sealed class Perfil
    {
        public int Manos, ManosJugadas, Subidas, Acciones, Showdowns, ShowdownsDebiles;
        /// <summary>VPIP: % de manos en que puso fichas voluntariamente preflop.</summary>
        public double Vpip => Manos == 0 ? 0.25 : ManosJugadas / (double)Manos;
        /// <summary>Agresión: % de sus acciones que son bet/raise.</summary>
        public double Agresion => Acciones == 0 ? 0.3 : Subidas / (double)Acciones;
    }
    private readonly Dictionary<string, Perfil> _rivales = new Dictionary<string, Perfil>();

    public void Initialize(string agentName) { }

    public ActionType GetAction(PlayerObservation o)
    {
        try
        {
            var reloj = Stopwatch.StartNew();
            int rivales = Math.Max(1, o.ActivePlayerCount - 1);
            double equity = EstimarEquity(o, rivales, reloj);
            double potOdds = o.PotOdds;   // ToCall / (Pot + ToCall)

            // Ajuste por rivales: si quien apuesta es muy agresivo (farolea), pagamos un poco más ligero.
            double ajuste = 0;
            var agresor = UltimoAgresor(o);
            if (agresor != null && _rivales.TryGetValue(agresor, out var p) && p.Acciones >= 20 && p.Agresion > 0.45) ajuste = 0.05;

            // v1: control de riesgo por posición en la mesa (ver RiesgoPorPosicion).
            double riesgo = RiesgoPorPosicion(o);

            double umbralSubida = 0.5 + 0.1 * (rivales - 1) / 4.0 + MargenSubida * riesgo;
            if (o.CanRaise && equity > umbralSubida) return ActionType.RAISE;
            if (o.ToCall == 0) return ActionType.CALL;       // check gratis: nunca foldear gratis
            if (equity + ajuste >= potOdds + MargenPago * riesgo) return ActionType.CALL;
            return ActionType.FOLD;
        }
        catch
        {
            return ActionType.CALL; // respaldo: el motor lo convierte en check si es gratis; si hay que pagar, pagamos
        }
    }

    /// <summary>Se llama al final de CADA mano (también si foldeaste). Aquí se aprende de los rivales.</summary>
    public void OnHandEnd(HandSummary s)
    {
        for (int seat = 0; seat < s.PlayerNames.Count; seat++)
        {
            if (seat == s.MySeat || s.StacksBefore[seat] == 0) continue;
            var nombre = s.PlayerNames[seat];
            if (!_rivales.TryGetValue(nombre, out var p)) _rivales[nombre] = p = new Perfil();
            p.Manos++;
            bool jugo = false;
            foreach (var a in s.Actions)
            {
                if (a.Seat != seat || a.Kind == ActionKind.SmallBlind || a.Kind == ActionKind.BigBlind) continue;
                p.Acciones++;
                if (a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise) p.Subidas++;
                if (a.Street == Street.Preflop && (a.Kind == ActionKind.Call || a.Kind == ActionKind.Raise)) jugo = true;
            }
            if (jugo) p.ManosJugadas++;

            // Cartas reveladas (solo si llegó al showdown): ¿llegó con poco? (ej. carta alta o pareja baja)
            var cartas = s.CardsOf(seat);
            if (cartas != null)
            {
                p.Showdowns++;
                if (HandEvaluator.GetCategory(s.HandValues[seat]) <= HandCategory.OnePair) p.ShowdownsDebiles++;
            }
        }
    }

    /// <summary>
    /// Devuelve un valor entre -1 y +1 según la posición en la mesa:
    ///   +1 = vamos clasificando con buena ventaja y quedan pocas manos → proteger (exigir más para pagar o subir).
    ///   -1 = vamos fuera de la zona de clasificación y se acaba la mesa → arriesgar más.
    ///    0 = principio de la mesa o situación ajustada → jugar normal.
    /// Las fichas de cada jugador se cuentan como al inicio de la mano (stack + lo ya apostado en ella).
    /// </summary>
    private static double RiesgoPorPosicion(PlayerObservation o)
    {
        var otros = new List<int>();
        int mias = 0;
        foreach (var p in o.Players)
        {
            int fichas = p.Stack + p.CommittedThisHand;
            if (p.Seat == o.Seat) mias = fichas; else otros.Add(fichas);
        }
        int clasifican = Math.Min(Clasifican, o.Players.Count - 1);
        if (clasifican < 1 || otros.Count < clasifican) return 0;
        otros.Sort((a, b) => b.CompareTo(a));            // de más a menos fichas
        int referencia = otros[clasifican - 1];            // si voy dentro: el mejor de los que quedan fuera; si voy fuera: el último que entra

        double avance = Math.Min(1.0, Math.Max(0.0, o.HandNumber / (double)ManosPorMesa));
        double diferencia = mias - referencia;
        if (diferencia > 0)                                 // dentro del top: proteger según colchón y avance
            return avance * Math.Min(1.0, diferencia / ColchonCompleto);
        return -avance;                                     // fuera del top: arriesgar más a medida que se acaba la mesa
    }

    private static string UltimoAgresor(PlayerObservation o)
    {
        for (int i = o.ActionHistory.Count - 1; i >= 0; i--)
        {
            var a = o.ActionHistory[i];
            if (a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise) return a.PlayerName;
        }
        return null;
    }

    // --- Rangos de rivales (v2) ---
    private const double RangoPrevio = 0.4;      // suposición inicial: un rival que entra al bote juega ~40 % de sus manos
    private const double PesoPrevio = 4;         // cuántas "manos imaginarias" pesa esa suposición
    private const double FactorSubida = 0.5;     // quien sube preflop juega la mitad más fuerte de su rango
    private const double RangoMinimo = 0.10;
    private const int MaxIntentos = 40;          // repartos por rival antes de aceptar cualquier mano

    // --- Lectura de apuestas postflop (v4) ---
    private const double ProbFarol = 0.30;        // de las manos que no ligaron nada, cuántas aceptamos (faroles y proyectos raros)
    private const double AgresionMaxima = 0.55;   // rivales más agresivos que esto apuestan con cualquier cosa: no se interpreta
    private const int AccionesMinimas = 10;       // acciones observadas antes de confiar en su agresión

    /// <summary>¿El rival apostó o subió después del flop en esta mano, y es alguien cuyas apuestas significan algo?</summary>
    private bool ApuestaConFuerza(PlayerObservation o, PublicPlayerState rival)
    {
        bool aposto = false;
        foreach (var a in o.ActionHistory)
            if (a.Seat == rival.Seat && a.Street != Street.Preflop && (a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise)) aposto = true;
        if (!aposto) return false;
        if (_rivales.TryGetValue(rival.Name, out var p) && p.Acciones >= AccionesMinimas && p.Agresion > AgresionMaxima) return false;
        return true;
    }

    /// <summary>¿Estas dos cartas "conectan" con el tablero visible? Pareja en mano, pareja con el tablero o proyecto de color.</summary>
    private static bool Conecta(int c0, int c1, int rangosMesa, int[] palosMesa)
    {
        int r0 = c0 / 4, r1 = c1 / 4;
        if (r0 == r1) return true;
        if ((rangosMesa & (1 << r0)) != 0 || (rangosMesa & (1 << r1)) != 0) return true;
        int s0 = c0 % 4, s1 = c1 % 4;
        if (palosMesa[s0] + (s0 == s1 ? 2 : 1) >= 4) return true;
        if (s0 != s1 && palosMesa[s1] + 1 >= 4) return true;
        return false;
    }

    /// <summary>Fuerza preflop aproximada de dos cartas (fórmula de Chen). Mayor = mejor.</summary>
    private static double Chen(int i0, int i1)
    {
        int r0 = i0 / 4 + 2, r1 = i1 / 4 + 2;
        bool mismoPalo = (i0 % 4) == (i1 % 4);
        int alta = Math.Max(r0, r1), baja = Math.Min(r0, r1);
        double v = alta == 14 ? 10 : alta == 13 ? 8 : alta == 12 ? 7 : alta == 11 ? 6 : alta / 2.0;
        if (alta == baja) return Math.Max(5, 2 * v);
        if (mismoPalo) v += 2;
        int hueco = alta - baja - 1;
        v -= hueco == 0 ? 0 : hueco == 1 ? 1 : hueco == 2 ? 2 : hueco == 3 ? 4 : 5;
        if (hueco <= 1 && alta < 12) v += 1;
        return v;
    }

    /// <summary>Puntajes de Chen de las 1326 manos posibles, de mayor a menor (dato fijo, nunca se modifica).</summary>
    private static readonly double[] ChenOrdenado = CrearChenOrdenado();
    private static double[] CrearChenOrdenado()
    {
        var lista = new List<double>(1326);
        for (int a = 0; a < 52; a++)
            for (int b = a + 1; b < 52; b++) lista.Add(Chen(a, b));
        lista.Sort((x, y) => y.CompareTo(x));
        return lista.ToArray();
    }

    /// <summary>Puntaje mínimo de Chen para estar en el mejor <paramref name="fraccion"/> de las manos.</summary>
    private static double UmbralChen(double fraccion)
    {
        if (fraccion >= 1) return double.MinValue;
        int idx = (int)Math.Ceiling(fraccion * ChenOrdenado.Length) - 1;
        return ChenOrdenado[Math.Max(0, Math.Min(ChenOrdenado.Length - 1, idx))];
    }

    /// <summary>Qué fracción de manos es razonable que tenga este rival, según su perfil y lo que hizo preflop en esta mano.</summary>
    private double FraccionRango(PlayerObservation o, PublicPlayerState rival)
    {
        bool subio = false, entro = false;
        foreach (var a in o.ActionHistory)
        {
            if (a.Seat != rival.Seat || a.Street != Street.Preflop) continue;
            if (a.Kind == ActionKind.Raise || a.Kind == ActionKind.Bet) subio = true;
            else if (a.Kind == ActionKind.Call) entro = true;
        }
        if (!subio && !entro) return 1.0;      // no puso fichas voluntariamente (p. ej. ciega grande que pasó): cualquier mano
        double jugadas = 0, manos = 0;
        if (_rivales.TryGetValue(rival.Name, out var p)) { jugadas = p.ManosJugadas; manos = p.Manos; }
        double vpip = (jugadas + RangoPrevio * PesoPrevio) / (manos + PesoPrevio);
        double f = subio ? vpip * FactorSubida : vpip;
        return Math.Max(RangoMinimo, Math.Min(1.0, f));
    }

    private double EstimarEquity(PlayerObservation o, int rivales, Stopwatch reloj)
    {
        var usadas = new bool[Card.DeckSize];
        foreach (var c in o.HoleCards) usadas[c.Index] = true;
        foreach (var c in o.Board) usadas[c.Index] = true;
        var mazo = new int[Card.DeckSize];
        int n = 0;
        for (int i = 0; i < Card.DeckSize; i++) if (!usadas[i]) mazo[n++] = i;

        // Umbral de fuerza preflop de cada rival que sigue en la mano.
        var umbrales = new List<double>();
        var exigeConexion = new List<bool>();
        bool postflop = o.Board.Count >= 3;
        foreach (var pl in o.Players)
            if (pl.Seat != o.Seat && pl.IsInHand && !pl.HasFolded)
            {
                umbrales.Add(UmbralChen(FraccionRango(o, pl)));
                exigeConexion.Add(postflop && ApuestaConFuerza(o, pl));
            }
        while (umbrales.Count < rivales) { umbrales.Add(double.MinValue); exigeConexion.Add(false); }
        rivales = umbrales.Count;

        int rangosMesa = 0;
        var palosMesa = new int[4];
        foreach (var c in o.Board) { rangosMesa |= 1 << (c.Index / 4); palosMesa[c.Index % 4]++; }

        var mesa = new Card[5];
        for (int i = 0; i < o.Board.Count; i++) mesa[i] = o.Board[i];
        int faltan = 5 - o.Board.Count;

        double puntos = 0; int sims = 0;
        while (sims < MaxSimulaciones && reloj.ElapsedMilliseconds < PresupuestoMs)
        {
            // 1) Cartas de cada rival, rechazando las que no encajan con su rango.
            int k = 0;
            for (int r = 0; r < rivales; r++)
            {
                for (int intento = 0; ; intento++)
                {
                    int j = k + _rng.Next(n - k); (mazo[k], mazo[j]) = (mazo[j], mazo[k]);
                    j = k + 1 + _rng.Next(n - k - 1); (mazo[k + 1], mazo[j]) = (mazo[j], mazo[k + 1]);
                    if (intento >= MaxIntentos) break;
                    if (Chen(mazo[k], mazo[k + 1]) < umbrales[r]) continue;
                    if (!exigeConexion[r] || Conecta(mazo[k], mazo[k + 1], rangosMesa, palosMesa) || _rng.NextDouble() < ProbFarol) break;
                }
                k += 2;
            }
            // 2) Cartas que faltan en la mesa.
            for (int b = 0; b < faltan; b++)
            {
                int j = k + _rng.Next(n - k); (mazo[k], mazo[j]) = (mazo[j], mazo[k]);
                mesa[o.Board.Count + b] = new Card(mazo[k]);
                k++;
            }

            int mio = HandEvaluator.Evaluate(o.HoleCards[0], o.HoleCards[1], mesa);
            int empates = 0;
            bool pierdo = false;
            for (int r = 0; r < rivales && !pierdo; r++)
            {
                int v = HandEvaluator.Evaluate(new Card(mazo[2 * r]), new Card(mazo[2 * r + 1]), mesa);
                if (v > mio) pierdo = true;
                else if (v == mio) empates++;
            }
            if (!pierdo) puntos += 1.0 / (1 + empates);
            sims++;
        }
        return sims == 0 ? 0.5 : puntos / sims;
    }
}
