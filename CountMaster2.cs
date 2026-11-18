using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgentsPoker.Core;

/// <summary>
/// Versión 2. Mejoras sobre la plantilla, todas con temas vistos en clase:
///  - Rollouts / planificación online (Clases 10 y 12): equity Monte Carlo en cada decisión.
///  - Modelo de rivales aprendido por promedio incremental (Clase 10): agresión, VPIP, showdowns.
///  - Rangos ponderados: el rival que apuesta ya no se simula con manos al azar sino con manos
///    más fuertes (mejor de m candidatas). Es una simulación de rollout con muestreo sesgado.
///  - Utilidad esperada (Clase 6, principio MEU): pagar si equity >= pot odds; subir si el equity
///    supera la parte "justa" 1/(n+1) por un margen, que baja con más rivales y sube si ya hubo subidas.
/// </summary>
public sealed class CountMasterV2 : IPokerAgent, IHandObserver
{
    private const int PresupuestoMs = 500;
    private const int MaxSimulaciones = 20000;
    private const string Rangos = "23456789TJQKA";

    private readonly Random _rng = new Random(2024);
    private string _yo = "";
    private readonly Card[] _cartas = new Card[Card.DeckSize];
    private readonly int[] _rango = new int[Card.DeckSize];
    private readonly char[] _palo = new char[Card.DeckSize];

    private sealed class Perfil
    {
        public int Manos, ManosJugadas, Subidas, Acciones, Showdowns, ShowdownsDebiles;
        public double Vpip => Manos == 0 ? 0.25 : ManosJugadas / (double)Manos;
        public double Agresion => Acciones == 0 ? 0.3 : Subidas / (double)Acciones;
    }
    private readonly Dictionary<string, Perfil> _rivales = new Dictionary<string, Perfil>();

    public CountMasterV2()
    {
        for (int i = 0; i < Card.DeckSize; i++)
        {
            _cartas[i] = new Card(i);
            string s = _cartas[i].ToString();
            _rango[i] = Math.Max(0, Rangos.IndexOf(s[0]));
            _palo[i] = s.Length > 1 ? s[1] : '?';
        }
    }

    public void Initialize(string agentName) { _yo = agentName ?? ""; }

    public ActionType GetAction(PlayerObservation o)
    {
        try
        {
            var reloj = Stopwatch.StartNew();
            int rivales = Math.Max(1, o.ActivePlayerCount - 1);

            int raisesOtros;
            List<int> mult = AnalizarHistorial(o, out raisesOtros);

            double equity = EstimarEquity(o, rivales, mult, reloj);
            double potOdds = o.PotOdds;
            if (o.Board.Count < 5) potOdds -= 0.02;   // pequeña compensación por implied odds

            // Ajuste según quién apuesta: maniaco -> pagamos más ligero; pasivo que apuesta -> más estricto
            double ajuste = 0;
            string agresor = UltimoAgresor(o);
            if (agresor != null && _rivales.TryGetValue(agresor, out var p) && p.Acciones >= 20)
            {
                if (p.Agresion > 0.45) ajuste = 0.05;
                else if (p.Agresion < 0.20) ajuste = -0.03;
            }

            // Umbral para subir: parte justa 1/(n+1) más un margen que baja con más rivales
            double justo = 1.0 / (rivales + 1);
            double umbralSubir = justo + (0.14 - 0.02 * (rivales - 1));
            umbralSubir += 0.02 * Math.Min(raisesOtros, 3);

            if (o.CanRaise && equity >= umbralSubir) return ActionType.RAISE;
            if (o.ToCall == 0) return ActionType.CALL;       // check gratis
            if (equity + ajuste >= potOdds) return ActionType.CALL;
            return ActionType.FOLD;
        }
        catch
        {
            return ActionType.CALL;
        }
    }

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

            var cartas = s.CardsOf(seat);
            if (cartas != null)
            {
                p.Showdowns++;
                if (HandEvaluator.GetCategory(s.HandValues[seat]) <= HandCategory.OnePair) p.ShowdownsDebiles++;
            }
        }
    }

    private string UltimoAgresor(PlayerObservation o)
    {
        for (int i = o.ActionHistory.Count - 1; i >= 0; i--)
        {
            var a = o.ActionHistory[i];
            if ((a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise) && a.PlayerName != _yo) return a.PlayerName;
        }
        return null;
    }

    /// <summary>Devuelve, por cada rival distinto que apostó/subió en esta mano, cuántas manos candidatas
    /// se sortean para él (más candidatas = rango más fuerte).</summary>
    private List<int> AnalizarHistorial(PlayerObservation o, out int raisesOtros)
    {
        var m = new List<int>();
        var vistos = new HashSet<string>();
        raisesOtros = 0;
        for (int i = 0; i < o.ActionHistory.Count; i++)
        {
            var a = o.ActionHistory[i];
            if (a.Kind != ActionKind.Bet && a.Kind != ActionKind.Raise) continue;
            if (a.PlayerName == _yo) continue;
            raisesOtros++;
            if (vistos.Add(a.PlayerName)) m.Add(Candidatas(a.PlayerName));
        }
        return m;
    }

    private int Candidatas(string nombre)
    {
        if (!_rivales.TryGetValue(nombre, out var p)) return 3;
        if (p.Showdowns >= 5 && p.ShowdownsDebiles / (double)p.Showdowns > 0.6) return 2; // llega con poco
        if (p.Acciones < 15) return 3;
        if (p.Agresion < 0.25) return 4;   // pasivo que apuesta: suele tener mano
        if (p.Agresion > 0.45) return 2;   // maniaco: rango ancho
        return 3;
    }

    private double EstimarEquity(PlayerObservation o, int rivales, List<int> mult, Stopwatch reloj)
    {
        var usadas = new bool[Card.DeckSize];
        foreach (var c in o.HoleCards) usadas[c.Index] = true;
        foreach (var c in o.Board) usadas[c.Index] = true;
        var mazo = new int[Card.DeckSize];
        int n = 0;
        for (int i = 0; i < Card.DeckSize; i++) if (!usadas[i]) mazo[n++] = i;

        var mesa = new Card[5];
        for (int i = 0; i < o.Board.Count; i++) mesa[i] = o.Board[i];
        int faltan = 5 - o.Board.Count;
        bool pre = o.Board.Count == 0;
        int necesarias = faltan + 2 * rivales;

        int nAgr = Math.Min(mult.Count, rivales);
        var m = new int[nAgr];
        int extra = 0;
        for (int i = 0; i < nAgr; i++) { m[i] = mult[i]; extra += 2 * (m[i] - 1); }
        if (necesarias + extra > n)
        {
            for (int i = 0; i < nAgr; i++) m[i] = 1;
            extra = 0;
        }
        var off = new int[nAgr];
        int acum = necesarias;
        for (int i = 0; i < nAgr; i++) { off[i] = acum; acum += 2 * (m[i] - 1); }
        int total = necesarias + extra;

        var h1 = new int[rivales];
        var h2 = new int[rivales];
        double puntos = 0; int sims = 0;
        while (sims < MaxSimulaciones && reloj.ElapsedMilliseconds < PresupuestoMs)
        {
            for (int k = 0; k < total; k++)
            {
                int j = k + _rng.Next(n - k);
                int t = mazo[k]; mazo[k] = mazo[j]; mazo[j] = t;
            }
            for (int k = 0; k < faltan; k++) mesa[o.Board.Count + k] = _cartas[mazo[k]];

            for (int r = 0; r < rivales; r++) { h1[r] = mazo[faltan + 2 * r]; h2[r] = mazo[faltan + 2 * r + 1]; }

            // Rivales que apostaron: nos quedamos con la mejor de m manos candidatas
            for (int r = 0; r < nAgr; r++)
            {
                double mejor = Puntaje(h1[r], h2[r], mesa, pre);
                for (int c = 0; c < m[r] - 1; c++)
                {
                    int a = mazo[off[r] + 2 * c], b = mazo[off[r] + 2 * c + 1];
                    double s = Puntaje(a, b, mesa, pre);
                    if (s > mejor) { mejor = s; h1[r] = a; h2[r] = b; }
                }
            }

            int mio = HandEvaluator.Evaluate(o.HoleCards[0], o.HoleCards[1], mesa);
            int empates = 0;
            bool pierdo = false;
            for (int r = 0; r < rivales && !pierdo; r++)
            {
                int v = HandEvaluator.Evaluate(_cartas[h1[r]], _cartas[h2[r]], mesa);
                if (v > mio) pierdo = true;
                else if (v == mio) empates++;
            }
            if (!pierdo) puntos += 1.0 / (1 + empates);
            sims++;
        }
        return sims == 0 ? 0.5 : puntos / sims;
    }

    private double Puntaje(int a, int b, Card[] mesa, bool pre)
    {
        if (pre) return Chen(a, b);
        return HandEvaluator.Evaluate(_cartas[a], _cartas[b], mesa);
    }

    /// <summary>Puntaje preflop tipo Chen: sirve solo para ordenar manos iniciales.</summary>
    private double Chen(int a, int b)
    {
        int ra = _rango[a], rb = _rango[b];
        int hi = Math.Max(ra, rb), lo = Math.Min(ra, rb);
        double p = PuntosCarta(hi);
        if (hi == lo) return Math.Max(5.0, p * 2);
        if (_palo[a] == _palo[b]) p += 2;
        int gap = hi - lo - 1;
        if (gap == 1) p -= 1;
        else if (gap == 2) p -= 2;
        else if (gap == 3) p -= 4;
        else if (gap >= 4) p -= 5;
        if (gap <= 1 && hi < 10) p += 1;
        return p;
    }

    private static double PuntosCarta(int r)
    {
        if (r == 12) return 10;
        if (r == 11) return 8;
        if (r == 10) return 7;
        if (r == 9) return 6;
        return (r + 2) / 2.0;
    }
}
