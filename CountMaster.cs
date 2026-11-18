using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgentsPoker.Core;

/// <summary>
/// CountMaster v5 — agente de Limit Texas Hold'em.
///
/// 1) EQUITY: Monte Carlo contra cada rival activo con RANGOS COHERENTES:
///      - preflop: fracción de manos (percentil de Chen) según el VPIP observado del rival, más estrecha si subió;
///      - postflop: si el rival apostó/subió, su mano debe "ligar" (clase de equivalencia) salvo faroles/proyectos.
///    Presupuesto de tiempo y parada anticipada cuando la decisión ya es clara.
/// 2) POLÍTICA: paga si equity >= pot odds efectivas (con implied odds); sube si equity >= 1/(rivales+1) + margen
///    (el margen crece con cada subida rival). Nunca hace FOLD si pasar es gratis.
/// 3) TORNEO: usa o.Match (ronda, final, manos restantes, cupos). En las últimas manos protege si va clasificando
///    (más cuanto mayor sea su colchón de fichas) y arriesga si va fuera; en la final solo vale el 1.º.
/// 4) APRENDIZAJE: perfiles por nombre (VPIP, agresión, fold-to-bet) en IHandObserver.OnHandEnd.
///
/// Todos los parámetros son campos públicos de instancia (no static): se pueden probar desde el laboratorio con
///   new CountMaster { ImplicitaCoef = 0.10 }
/// </summary>
public sealed class CountMaster : IPokerAgent, IHandObserver
{
    // =====================================================================================================
    //  PARÁMETROS
    // =====================================================================================================

    // ---- Tiempo y precisión del Monte Carlo ----
    /// <summary>Tiempo máximo por decisión (ms). Límite real 1000; la Arena (Mono) es más lenta: no pasar de ~300.</summary>
    public int PresupuestoMs = 180;
    public int MaxSimulaciones = 6000;
    public int MinSimulaciones = 600;

    // ---- SUBIR: raise si equity >= 1/(rivales+1) + margen + penalización por subidas rivales ----
    public double MargenSubidaPreflop = 0.13;
    public double MargenSubidaFlop = 0.15;
    public double MargenSubidaTurn = 0.15;
    public double MargenSubidaRiver = 0.12;
    public double PenalizacionPorSubidaRival = 0.04;

    // ---- PAGAR: equity >= ToCall / (Pot + ToCall + extra de implied odds) ----
    /// <summary>Fracción de las apuestas futuras (por rival) que se suma al bote. 0 = pot odds puras. Más alto = paga más suelto.</summary>
    public double ImplicitaCoef = 0.12;
    public double HolguraPago = 0.0;
    /// <summary>Bonus de pago en el botón (posición).</summary>
    public double BonusPosicion = 0.01;

    // ---- Rango preflop de los rivales (de la v1 de CountMaster) ----
    /// <summary>Si false: los rivales se simulan con cualquier mano preflop (ablación).</summary>
    public bool UsarRangoPreflop = true;
    /// <summary>Suposición inicial: un rival que entra al bote juega ~40 % de sus manos.</summary>
    public double RangoPrevio = 0.4;
    /// <summary>Cuántas "manos imaginarias" pesa esa suposición.</summary>
    public double PesoPrevio = 4;
    /// <summary>Quien sube preflop juega esta fracción de su rango (se aplica una vez por subida, máx. 2).</summary>
    public double FactorSubida = 0.5;
    public double RangoMinimo = 0.10;

    // ---- Lectura de apuestas postflop ----
    /// <summary>Clase de equivalencia máxima (menor = mejor) de quien apostó postflop: 1 apuesta / 2 / 3+. 5000≈par medio, 3400≈doble par, 2500≈trío.</summary>
    public int ClaseNivel1 = 5000, ClaseNivel2 = 3400, ClaseNivel3 = 2500;
    /// <summary>Probabilidad de aceptar una mano que no liga (proyectos / faroles).</summary>
    public double ProbManoDebil = 0.10;
    public int MaxIntentosRango = 20;
    /// <summary>Rival con agresión >= esto (y muestra suficiente) apuesta con cualquier cosa: sus apuestas no estrechan el rango.</summary>
    public double UmbralManiaco = 0.55;
    /// <summary>Rival con VPIP <= esto es "roca": sus apuestas postflop estrechan más el rango.</summary>
    public double UmbralRoca = 0.20;
    public int MuestraMinima = 12;

    // ---- Farol ----
    public double FrecuenciaFarol = 0.10;
    public double FoldMinimoParaFarol = 0.55;

    // ---- Torneo ----
    /// <summary>Manos finales en las que crece el efecto (rampa lineal hasta la última). 30 = toda la mesa.</summary>
    public int ManosCriticas = 30;
    public double ProteccionSube = 0.05, ProteccionPaga = 0.03;
    public double PersecucionSube = 0.05, PersecucionPaga = 0.03;
    /// <summary>Multiplicador de arriesgar en la MESA FINAL si no voy 1.º.</summary>
    public double FactorFinal = 1.5;
    /// <summary>Ventaja en fichas (sobre el último que clasifica fuera de mí) desde la cual se protege al máximo.</summary>
    public double ColchonCompleto = 400;

    // =====================================================================================================
    //  ESTADO INTERNO
    // =====================================================================================================
    private readonly Random _rng;
    private readonly Card[] _todas = new Card[Card.DeckSize];
    private readonly double[] _chen = new double[Card.DeckSize * Card.DeckSize]; // Chen de cada par de cartas
    private readonly double[] _chenOrdenado;                                     // las 1326 manos, de mayor a menor

    public CountMaster()
    {
        _rng = new Random(12345); // semilla fija: partidas reproducibles al depurar
        for (int i = 0; i < Card.DeckSize; i++) _todas[i] = new Card(i);
        var lista = new List<double>(1326);
        for (int a = 0; a < Card.DeckSize; a++)
            for (int b = a + 1; b < Card.DeckSize; b++)
            {
                double c = Chen(_todas[a], _todas[b]);
                _chen[a * Card.DeckSize + b] = c;
                _chen[b * Card.DeckSize + a] = c;
                lista.Add(c);
            }
        lista.Sort((x, y) => y.CompareTo(x));
        _chenOrdenado = lista.ToArray();
    }

    private sealed class Perfil
    {
        public int Manos, ManosJugadas, Acciones, Subidas, Enfrentadas, Foldeadas;
        public double Vpip { get { return Manos == 0 ? 0.30 : ManosJugadas / (double)Manos; } }
        public double Agresion { get { return Acciones == 0 ? 0.30 : Subidas / (double)Acciones; } }
        public double FoldRate { get { return Enfrentadas == 0 ? 0.40 : Foldeadas / (double)Enfrentadas; } }
    }
    private readonly Dictionary<string, Perfil> _perfiles = new Dictionary<string, Perfil>();

    public void Initialize(string agentName) { }

    // =====================================================================================================
    //  DECISIÓN
    // =====================================================================================================
    public ActionType GetAction(PlayerObservation o)
    {
        try
        {
            var reloj = Stopwatch.StartNew();

            int rivales;
            double[] chenMin; int[] nivelPost;
            PrepararRivales(o, out rivales, out chenMin, out nivelPost);
            if (rivales == 0) return o.ToCall == 0 ? ActionType.CALL : ActionType.FOLD;

            int calle = (int)o.Street; // Preflop=0, Flop=1, Turn=2, River=3
            double justo = 1.0 / (rivales + 1);
            double margen = calle == 0 ? MargenSubidaPreflop : calle == 1 ? MargenSubidaFlop : calle == 2 ? MargenSubidaTurn : MargenSubidaRiver;
            int subidasRivales = o.Street == Street.Preflop ? Math.Max(0, o.BetsThisStreet - 1) : o.BetsThisStreet;
            double umbralSubir = justo + margen + PenalizacionPorSubidaRival * (o.ToCall > 0 ? subidasRivales : 0);

            double potOddsEfectivas = 0;
            if (o.ToCall > 0)
            {
                double extra = ImplicitaCoef * FuturasApuestas(o) * rivales;
                potOddsEfectivas = o.ToCall / (double)(o.Pot + o.ToCall + extra);
            }
            double umbralPagar = potOddsEfectivas - HolguraPago - (o.Me != null && o.Me.IsButton ? BonusPosicion : 0.0);

            AjusteTorneo(o, ref umbralSubir, ref umbralPagar);

            double equity = EstimarEquity(o, rivales, chenMin, nivelPost, reloj, umbralSubir, umbralPagar);

            if (o.CanRaise && equity >= umbralSubir) return ActionType.RAISE;
            if (o.ToCall == 0)
            {
                if (o.CanRaise && DebeFarolear(o, rivales, equity)) return ActionType.RAISE;
                return ActionType.CALL; // check gratis: nunca foldear gratis
            }
            return equity >= umbralPagar ? ActionType.CALL : ActionType.FOLD;
        }
        catch
        {
            return o.ToCall == 0 ? ActionType.CALL : ActionType.FOLD;
        }
    }

    private static double FuturasApuestas(PlayerObservation o)
    {
        switch (o.Street)
        {
            case Street.Preflop: return 2 * o.BetSize + 2 * 2 * o.BetSize; // 20+20+40+40
            case Street.Flop: return o.BetSize + 2 * o.BetSize * 2;       // 20+40+40
            case Street.Turn: return o.BetSize * 2;                        // 40+40
            default: return o.BetSize;                                     // 40
        }
    }

    // =====================================================================================================
    //  TORNEO: protección / riesgo según el puesto, el colchón de fichas y las manos que quedan
    // =====================================================================================================
    private void AjusteTorneo(PlayerObservation o, ref double umbralSubir, ref double umbralPagar)
    {
        var m = o.Match;
        if (m == null || !m.HasHandLimit || ManosCriticas <= 0) return;
        int quedan = m.HandsRemaining;                 // cuenta la mano actual
        if (quedan > ManosCriticas || quedan < 1) return;

        double rampa = (ManosCriticas - quedan + 1) / (double)ManosCriticas; // 0..1
        int cupos = m.IsFinalTable ? 1 : (m.QualifyingSpots > 0 ? m.QualifyingSpots : 3);

        var otros = new List<int>();
        int mias = o.MyStack + o.MyCommittedThisHand;
        foreach (var p in o.Players)
            if (p.Seat != o.Seat) otros.Add(p.Stack + p.CommittedThisHand);
        if (otros.Count < cupos) return;
        otros.Sort((a, b) => b.CompareTo(a));
        int referencia = otros[cupos - 1]; // dentro: el mejor que queda fuera; fuera: el último que entra

        int diferencia = mias - referencia;
        if (diferencia > 0)
        {
            double colchon = Math.Min(1.0, diferencia / Math.Max(1.0, ColchonCompleto));
            umbralSubir += ProteccionSube * rampa * colchon;
            umbralPagar += ProteccionPaga * rampa * colchon;
        }
        else
        {
            double f = m.IsFinalTable ? FactorFinal : 1.0;
            umbralSubir -= PersecucionSube * rampa * f;
            umbralPagar -= PersecucionPaga * rampa * f;
        }
    }

    // =====================================================================================================
    //  FAROL
    // =====================================================================================================
    private bool DebeFarolear(PlayerObservation o, int rivales, double equity)
    {
        if (FrecuenciaFarol <= 0 || o.Street != Street.River || rivales > 2 || equity > 0.40) return false;
        foreach (var p in o.Players)
        {
            if (p.Seat == o.Seat || !p.IsActive) continue;
            Perfil pf;
            if (!_perfiles.TryGetValue(p.Name, out pf) || pf.Enfrentadas < 5 || pf.FoldRate < FoldMinimoParaFarol) return false;
        }
        return _rng.NextDouble() < FrecuenciaFarol;
    }

    // =====================================================================================================
    //  RANGOS DE LOS RIVALES
    // =====================================================================================================
    private void PrepararRivales(PlayerObservation o, out int rivales, out double[] chenMin, out int[] nivelPost)
    {
        var cm = new List<double>(5);
        var np = new List<int>(5);
        foreach (var p in o.Players)
        {
            if (p.Seat == o.Seat || !p.IsActive) continue;

            int subidasPre = 0, subidasPost = 0;
            bool entro = false;
            foreach (var a in o.ActionHistory)
            {
                if (a.Seat != p.Seat) continue;
                bool sube = a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise;
                if (a.Street == Street.Preflop) { if (sube) subidasPre++; else if (a.Kind == ActionKind.Call) entro = true; }
                else if (sube) subidasPost++;
            }

            Perfil pf;
            bool tienePerfil = _perfiles.TryGetValue(p.Name, out pf);
            bool maniaco = tienePerfil && pf.Acciones >= MuestraMinima && pf.Agresion >= UmbralManiaco;
            bool roca = tienePerfil && pf.Manos >= MuestraMinima && pf.Vpip <= UmbralRoca;

            // --- rango preflop: percentil de Chen según VPIP (con prior bayesiano) ---
            double umbralChen = double.MinValue;
            if (UsarRangoPreflop && (subidasPre > 0 || entro))
            {
                double jugadas = tienePerfil ? pf.ManosJugadas : 0, manos = tienePerfil ? pf.Manos : 0;
                double vpip = (jugadas + RangoPrevio * PesoPrevio) / (manos + PesoPrevio);
                double f = vpip;
                if (!maniaco) for (int i = 0; i < Math.Min(2, subidasPre); i++) f *= FactorSubida;
                umbralChen = UmbralChen(Math.Max(RangoMinimo, Math.Min(1.0, f)));
            }
            cm.Add(umbralChen);

            // --- nivel postflop (0 = rango amplio) ---
            int nivel = Math.Min(3, subidasPost);
            if (nivel > 0)
            {
                if (maniaco) nivel = 0;
                else if (roca) nivel = Math.Min(3, nivel + 1);
            }
            np.Add(nivel);
        }
        rivales = cm.Count;
        chenMin = cm.ToArray();
        nivelPost = np.ToArray();
    }

    /// <summary>Puntaje mínimo de Chen para estar en el mejor <paramref name="fraccion"/> de las manos.</summary>
    private double UmbralChen(double fraccion)
    {
        if (fraccion >= 1) return double.MinValue;
        int idx = (int)Math.Ceiling(fraccion * _chenOrdenado.Length) - 1;
        return _chenOrdenado[Math.Max(0, Math.Min(_chenOrdenado.Length - 1, idx))];
    }

    // =====================================================================================================
    //  EQUITY POR MONTE CARLO (rangos ponderados)
    // =====================================================================================================
    private double EstimarEquity(PlayerObservation o, int rivales, double[] chenMin, int[] nivelPost,
                                 Stopwatch reloj, double umbralSubir, double umbralPagar)
    {
        var usadas = new bool[Card.DeckSize];
        foreach (var c in o.HoleCards) usadas[c.Index] = true;
        foreach (var c in o.Board) usadas[c.Index] = true;
        var mazo = new int[Card.DeckSize];
        int n = 0;
        for (int i = 0; i < Card.DeckSize; i++) if (!usadas[i]) mazo[n++] = i;

        var mesa = new Card[5];
        int bc = o.Board.Count;
        for (int i = 0; i < bc; i++) mesa[i] = o.Board[i];
        int faltan = 5 - bc;
        var visible = new Card[bc];                        // board que ven los rivales al apostar
        for (int i = 0; i < bc; i++) visible[i] = o.Board[i];
        Card h0 = o.HoleCards[0], h1 = o.HoleCards[1];

        var restringido = new bool[rivales];
        for (int r = 0; r < rivales; r++) restringido[r] = chenMin[r] > double.MinValue || nivelPost[r] > 0;

        double puntos = 0;
        int sims = 0;
        while (sims < MaxSimulaciones)
        {
            if ((sims & 31) == 0)
            {
                if (reloj.ElapsedMilliseconds >= PresupuestoMs) break;
                if (sims >= MinSimulaciones && (sims % 200) == 0)
                {
                    double e = puntos / sims;
                    double se = Math.Sqrt(Math.Max(1e-6, e * (1 - e)) / sims);
                    bool claroPagar = Math.Abs(e - umbralPagar) > 4 * se;
                    bool claroSubir = !o.CanRaise || Math.Abs(e - umbralSubir) > 4 * se;
                    if (claroPagar && claroSubir) break;
                }
            }

            int pos = 0;
            for (int k = 0; k < faltan; k++)
            {
                int j = pos + _rng.Next(n - pos);
                int t = mazo[pos]; mazo[pos] = mazo[j]; mazo[j] = t;
                mesa[bc + k] = _todas[mazo[pos]];
                pos++;
            }

            int mio = HandEvaluator.Evaluate(h0, h1, mesa);
            int empates = 0;
            bool pierdo = false;
            for (int r = 0; r < rivales && !pierdo; r++)
            {
                int a, b;
                SacarMano(mazo, n, ref pos, restringido[r], chenMin[r], nivelPost[r], visible, bc, out a, out b);
                int v = HandEvaluator.Evaluate(_todas[a], _todas[b], mesa);
                if (v > mio) pierdo = true;
                else if (v == mio) empates++;
            }
            if (!pierdo) puntos += 1.0 / (1 + empates);
            sims++;
        }
        return sims == 0 ? 0.5 : puntos / sims;
    }

    /// <summary>Saca 2 cartas para un rival; si tiene rango restringido, rejection sampling (Chen preflop + clase postflop).</summary>
    private void SacarMano(int[] mazo, int n, ref int pos, bool restringido, double chenMin, int nivel,
                           Card[] visible, int boardCount, out int a, out int b)
    {
        int intentos = restringido ? MaxIntentosRango : 1;
        int claseMax = nivel == 1 ? ClaseNivel1 : nivel == 2 ? ClaseNivel2 : ClaseNivel3;
        for (int intento = 0; intento < intentos; intento++)
        {
            int j1 = pos + _rng.Next(n - pos);
            int t1 = mazo[pos]; mazo[pos] = mazo[j1]; mazo[j1] = t1;
            int j2 = pos + 1 + _rng.Next(n - pos - 1);
            int t2 = mazo[pos + 1]; mazo[pos + 1] = mazo[j2]; mazo[j2] = t2;
            if (!restringido) break;

            if (_chen[mazo[pos] * Card.DeckSize + mazo[pos + 1]] < chenMin) continue; // rango preflop (estricto)
            if (nivel > 0 && boardCount > 0)
            {
                int clase = HandEvaluator.ToEquivalenceClass(HandEvaluator.Evaluate(_todas[mazo[pos]], _todas[mazo[pos + 1]], visible));
                if (clase > claseMax && _rng.NextDouble() >= ProbManoDebil) continue;   // no ligó: solo faroles/proyectos
            }
            break;
        }
        a = mazo[pos]; b = mazo[pos + 1];
        pos += 2;
    }

    /// <summary>Puntaje de Chen (fuerza preflop aproximada): AA=20, KK=16, AKs=12, 22=5...</summary>
    private static double Chen(Card x, Card y)
    {
        int hi = Math.Max(x.Rank, y.Rank), lo = Math.Min(x.Rank, y.Rank);
        double p = hi == 14 ? 10 : hi == 13 ? 8 : hi == 12 ? 7 : hi == 11 ? 6 : hi / 2.0;
        if (hi == lo) return Math.Max(5, p * 2);
        if (x.Suit == y.Suit) p += 2;
        int gap = hi - lo - 1;
        if (gap == 1) p -= 1; else if (gap == 2) p -= 2; else if (gap == 3) p -= 4; else if (gap >= 4) p -= 5;
        if (gap <= 1 && hi < 12) p += 1;
        return p;
    }

    // =====================================================================================================
    //  APRENDIZAJE DE RIVALES
    // =====================================================================================================
    public void OnHandEnd(HandSummary s)
    {
        try
        {
            for (int seat = 0; seat < s.PlayerNames.Count; seat++)
            {
                if (seat == s.MySeat || s.StacksBefore[seat] == 0) continue;
                string nombre = s.PlayerNames[seat];
                Perfil p;
                if (!_perfiles.TryGetValue(nombre, out p)) { p = new Perfil(); _perfiles[nombre] = p; }
                p.Manos++;

                bool jugo = false;
                int apuestasEnCalle = 0;
                Street calleActual = Street.Preflop;
                foreach (var a in s.Actions)
                {
                    if (a.Street != calleActual) { calleActual = a.Street; apuestasEnCalle = 0; }
                    bool esBlind = a.Kind == ActionKind.SmallBlind || a.Kind == ActionKind.BigBlind;
                    if (a.Seat == seat && !esBlind)
                    {
                        p.Acciones++;
                        if (a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise) p.Subidas++;
                        if (a.Street == Street.Preflop && (a.Kind == ActionKind.Call || a.Kind == ActionKind.Raise)) jugo = true;
                        if (a.Street != Street.Preflop && apuestasEnCalle > 0 &&
                            (a.Kind == ActionKind.Fold || a.Kind == ActionKind.Call || a.Kind == ActionKind.Raise))
                        {
                            p.Enfrentadas++;
                            if (a.Kind == ActionKind.Fold) p.Foldeadas++;
                        }
                    }
                    if (a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise) apuestasEnCalle++;
                }
                if (jugo) p.ManosJugadas++;
            }
        }
        catch { /* nunca propagar excepciones */ }
    }
}
