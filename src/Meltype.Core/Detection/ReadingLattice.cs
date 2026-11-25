// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 aster-void

using System.Runtime.InteropServices;
using Meltype.Libraries.Chars;

namespace Meltype.Detection;

/// <summary>
/// 日英の区切り。日本語の語・英単語・未知語をノードにしたラティスを作り、Viterbi で最小コスト経路を選ぶ。
/// 日本語の語の生起コストは RomajiTypoCorrector.WordCost と共有する。
///
/// コストで比べない決まり:
/// - 大文字を含む区間は日本語のノードにしない。
/// - 綴りを名指しする条件は持たない。
/// - 知っている英単語は、英単語の書き方の大文字・小文字 (its・Its・ITS・GitHub) のときだけノードにする (iTs は its にしない)。
/// - 1 文字の英単語 (a・i・u・r) は、前後を英字以外で区切ったときだけノードにする。
/// - 自動判定を使わないなら、Shift を押して打った大文字で始まる語だけを英語のノードにする。
/// - 記号・数字の決まり (ドメイン・拡張子・ユーザー名・- の入った語・数字を挟んだ略語・英文の中の記号) は、コストで比べずにその区間を英語にする。
/// - 技術用語の綴りのすぐ後ろが する の活用 (share|して・make|します) なら、その綴りを日本語の語 (しゃれ・まけ) として読まない (しゃれする・まけします は日本語でない)。ただし一段動詞の使役形 (make|させた) は日本語のまま。
/// - ローマ字入力では 1 文字ずつを区切りの位置にする (単位の途中で区切れる: get|Element、medal|s|ha)。かな入力では打った単位の境目だけ。
/// </summary>
internal sealed partial class CompositionDetector
{
    // ---- 日本語のノードの生起コスト ----
    // 1 文字の語 1.5・付属語 0.8 は RomajiTypoCorrector.WordCost
    // 日本語の語 (readings.txt・readings-extra.txt と活用した形)・同梱の語句の読み (phrases.txt の どしたの)・
    // ユーザーがかなに直して覚えた語・english.txt の日本語由来の語 (sushi) をローマ字で読んだかな
    private const double JapaneseWordCost = 1;
    // katakana.txt の語 (ほすてぃんぐ) と brands.txt の読み (ぎっとはぶ)。katakana.txt は英単語をカタカナにした語 (アマゾン) が多いので、同じ綴りの英単語 (amazon) に譲る
    private const double KatakanaWordCost = 1.1;
    // readings-rare.txt の語 (JMdict のよく使う語でない語の 3 文字以上の読み: ろんぱ・かんれんづけ・くるまどめ)。
    // 英単語への切り替え (関連|duke = 1 + 1 + 1) より軽く、よく使う語どうしの区切りを割らない重みにする。
    // 1.5 では きょうはい|まげ が きょうは|image より軽くなり、3 では 関連づけ が 関連|duke と同じ重みになる
    private const double RareWordCost = 2.5;
    // readings-rare.txt の 4 文字以下の読み (活用形も含む) は、英単語の一部に重なりやすい (ぜん|ぶち|めにする)。
    // 日本語の前後に打った英単語 (ぜんぶ|time|にする) より軽くせず、未知音 + 英語 (うち|dome = 3) よりは軽くする。
    private const double ShortRareWordCost = 2.9;
    // 語にならないかな 1 音、日本語の中に残る読めない英字 1 文字
    internal const double UnknownKanaCost = 3;
    // 語にならない っ・ー、x で打った小書きの母音が語の 1 音目 (だめ|ぇ)
    internal const double LightKanaCost = 0.5;

    // ---- 英語のノードの生起コスト ----
    // 知っている英単語 (english.txt・固有名詞・english-words.txt)
    private const double KnownEnglishCost = 1;
    // english-readable.txt の語 (open・feature): ローマ字としても読めるが、英語で打つことの多い語
    private const double ReadableEnglishCost = 0.7;
    // ユーザーが英字に直して覚えた語。同じ読みの日本語の語 (sushi = すし、重み 1) より軽くする
    private const double LearnedEnglishCost = 0.7;
    // 小文字の 2 文字以下の知っている英単語 (be・pa・on) に足す。ローマ字の 1 音と同じ綴りなので、辞書にない日本語の語 (すべ・ろんぱ) が
    // 語にならないかな + 短い英単語 + 語にならないかな (す|be|を・ろん|pa) に割れていた。
    // 大文字を含む語 (AI・Go) は日本語のノードと取り合わないので足さない。覚えた語にも足さない
    private const double ShortEnglishCost = 1;
    // 知らない英字 (ローマ字として読めない所: pwd) 1 文字
    private const double UnknownLetterCost = 1.5;
    // 入力の最後の、知っている英単語の頭 (insta)。英単語 + 日本語 1 音 (ins + た) より軽く、日本語の語より重い
    private const double EnglishPrefixCost = 2.5;
    // ローマ字として読める知らない語 + . + ドメイン・拡張子 (hoge.io・llama.cpp) をまとめた区間は、知っている英単語の重み + . の前の 1 文字ごとにこの重み
    // (前が長いほど、句点の後ろの日本語 junbigadekita.|deha と読む方が自然になる)
    private const double ReadableDomainLetterCost = 0.25;
    // 知らない語の大文字 1 文字
    private const double UnknownUpperCost = 1;
    // 大文字で始まり 2 文字目が小文字の 3 文字以上の知らない語 (Tanaka・McKinsey) の小文字 1 文字
    private const double NameLetterCost = 0.5;
    // かな入力の 4 文字以上の知っている英単語に足す: 英字のキーで打ったかな (さっき + かく|ちか = that) が日本語の語に読める分を打ち消す。
    // 3 文字以下 (eh・at・id) は日本語の文の一部が英単語になりやすいので付けない
    private const double KanaInputEnglishBias = -1;
    // 変換の候補に出す英語寄りの区切りを作るとき英語のノードごとに足す: 英単語 + 切り替え + 付属語 (midi + か = 2.8) が日本語の語 1 つ (みぢか = 1) より軽くなる
    private const double LeaningEnglishBias = -2;

    // ---- 連接コスト (重みは LinkWeights) ----
    // 日本語の中の c 行の綴り (ca・ci・cu・ce・co。ch は除く) 1 つ: 日本語を打つときは k を使う
    private const double CRowCost = 0.5;
    // 同じコストなら日本語にする
    private const double EnglishTieBreak = 1e-6;
    // 1 つの候補として見る長さの上限 (ローマ字の文字数)
    private const int MaxSpan = 48;

    // ---- 変換の候補に出す区切りの別案 ----
    // 最も軽い案からこれより重い案は出さない
    private const double AlternativeMargin = 1;
    // (位置, 状態) ごとに残す道の数。日本語の語の区切りだけが違う道 (ぼ|っと・ぼっと) が同じ案になるので、出す案の数より多く残す
    private const int PathsPerState = 8;

    private enum Side : byte { None, Japanese, English, Sentence }

    /// <summary>区切りの 1 つの候補。Kana は日本語の候補の読み。Letters は英字を含む日本語の候補か。Neutral は記号・数字・空白 (言語を切り替えない)。</summary>
    private readonly record struct LatticeEdge(int Start, int End, bool English, string Kana, bool Letters, bool Particle, bool Neutral = false, bool Name = false);

    private readonly record struct LatticeNode(double Cost, int Prev, LatticeEdge Edge, bool Sandwiched = false);

    // 道のノードと到達した状態の表を、打鍵をまたいで使う。道は配列の添字でつなぐ。
    private LatticeNode[] _latticeNodes = [];
    private UInt128[] _reached = [];
    private int[] _best = [], _bestStamp = [];
    private int _bestGeneration;

    private readonly record struct LinkEdge(LatticeEdge Edge, double Cost, bool BetweenLetters, bool VowelAfter, bool NameSplit,
        bool Sokuon, bool DanglingSokuon, bool NounParticleStart, bool VerbAuxiliary, int SpelledSokuons);

    private readonly record struct ReadingContext(LanguageMemory? Memory, int MemoryVersion, bool? Learning, bool AfterEnglish, bool KanaInput, bool Manual);

    // 候補と接続に使う値を一緒に保つ。候補が読んだ範囲か、接続に必要な隣の文字が変わったら組み直す。
    private sealed class EdgeCache
    {
        public readonly List<(LatticeEdge Edge, double Cost)> Candidates = [];
        public readonly List<LinkEdge> Links = [];
        // ReadTo は最後に読んだ文字の次の位置。入力が終わったことも判定に使った場合は length + 1。
        public int ReadTo;
        public uint Generation;

        public List<(LatticeEdge Edge, double Cost)> Start(int readTo)
        {
            Candidates.Clear();
            Generation = 0;
            ReadTo = readTo;
            return Candidates;
        }
    }

    private EdgeCache?[] _japaneseEdges = [], _englishEdges = [];
    private uint[] _readingChanged = [];
    private uint _readingGeneration;
    private ReadingContext _readingContext;
    private string _readingText = "";
    private CompositionUnit[] _readingUnits = [];
    private int[] _readingUnreadable = [];
    private int _readingUnitCount;

    // 促音の数は辺ごとの走査を省くため、表にして区間の両端から求める。
    private byte[] _sokuonLen = [];
    private int[] _sokuonPrefix = [];
    private int[] _letterRunEnd = [], _unitLetterRunEnd = [], _unitPartRunEnd = [], _unitDigitsAfter = [];

    /// <summary>
    /// 状態: 直前の言語 (Side)・直前の日本語の候補が名詞に付く付属語か・ここまで英字の日本語が無いか・直前の候補が英語か・直前の候補が っ で終わるか・
    /// 前の英語の文脈 (hello ) からの切り替えをまだ数えていないか。記号・数字の候補は直前の言語を変えない。
    /// </summary>
    private readonly record struct LatticeState(Side Side, bool Particle, bool AllEnglish, bool PreviousEnglish, bool Sokuon = false, bool ContextSwitch = false)
    {
        public const int Count = 128;
        public int Key => (int)Side | (Particle ? 4 : 0) | (AllEnglish ? 8 : 0) | (PreviousEnglish ? 16 : 0) | (Sokuon ? 32 : 0) | (ContextSwitch ? 64 : 0);
        public static LatticeState FromKey(int key) => new((Side)(key & 3), (key & 4) != 0, (key & 8) != 0, (key & 16) != 0, (key & 32) != 0, (key & 64) != 0);
        public bool IsEnglish => Side is Side.English or Side.Sentence;
    }

    /// <summary>コストで比べずに、その区間を英語にする記号・数字の決まり (<see cref="CompositionDetector"/> の説明の一覧)。</summary>
    private enum LexemeRule { None, Dotfile, UserName, HyphenatedWord, Numeronym, ProperNounWithDigits, VersionSuffix }

    /// <summary>
    /// 1 つの位置から始まる字句。Rule が None でなければ、End (単位の番号) までを英語にする。
    /// DomainEnd はローマ字として読める語 + ドメイン・拡張子の終わり (重みで比べる)、Technical は技術用語の綴り + する の活用の頭。
    /// </summary>
    private readonly record struct Lexeme(LexemeRule Rule, int End, int DomainEnd, bool Technical);

    /// <summary>辺をつなぐときに数える特徴の、1 回ごとの重み。</summary>
    internal LinkWeights Weights { get; private init; } = LinkWeights.Default;

    /// <summary>調べる用 (--segments): 区間分けのたびに、最後の状態ごとの一番軽い道と、候補ごとの重み (+) を 1 行ずつ渡す。* が選んだ道。</summary>
    internal Action<string>? LatticeTrace { get; set; }

    /// <param name="reasons">渡したら、選んだ道と、言語の割り当てが違う一番軽い道 (次点) を書く関数を足す (確定のログ用。issue #1644)。
    /// キーを打つたびに呼ばれるので、次点を探して文字列にするのは、ログに書くときだけにする。</param>
    private List<CompositionSegment> FindSpansByReading(Prepared prepared, bool? learning, IReadOnlyList<CompositionUnit> units, string pending, bool? precedingEnglish, bool? followingEnglish,
        bool manual, bool englishSentence, bool kanaInput, double englishBias = 0, List<Func<string>>? reasons = null) =>
        FindSpanAlternatives(prepared, learning, units, pending, precedingEnglish, followingEnglish, manual, englishSentence, kanaInput, englishBias, 1, reasons)[0];

    /// <summary>
    /// 区切りの上位 count 案を、軽い順に返す。先頭は <see cref="FindSpansByReading"/> と同じ。区間の並びが同じ道 (日本語の語の区切りだけが違う道) は 1 案にまとめる。
    /// count が 2 以上なら、(位置, 状態) ごとに軽い道を <see cref="PathsPerState"/> 個ずつ残す (k-best Viterbi)。最も軽い案から <see cref="AlternativeMargin"/> より重い案は返さない。
    /// reasons には、いちばん良い案を選んだ理由を足す。
    /// </summary>
    private List<List<CompositionSegment>> FindSpanAlternatives(Prepared prepared, bool? learning, IReadOnlyList<CompositionUnit> units, string pending, bool? precedingEnglish, bool? followingEnglish,
        bool manual, bool englishSentence, bool kanaInput, double englishBias, int count, List<Func<string>>? reasons = null)
    {
        var text = prepared.Raw + pending;
        var length = text.Length;
        if (length == 0) return [[new CompositionSegment(false, "", "")]];
        var keep = count > 1 ? PathsPerState : 1;
        var unitsEnd = prepared.Raw.Length;
        // 文字の位置 → その位置から始まる単位 (単位の境目でなければ -1)
        var unitAt = new int[length + 1];
        Array.Fill(unitAt, -1);
        for (var k = 0; k < units.Count; k++) unitAt[prepared.RawOffsets[k]] = k;
        unitAt[unitsEnd] = units.Count;
        // 文字の位置 → その文字を含む単位
        var unitOf = new int[unitsEnd];
        for (var k = 0; k < units.Count; k++)
            for (var p = prepared.RawOffsets[k]; p < prepared.RawOffsets[k + 1]; p++) unitOf[p] = k;
        bool IsBoundary(int position) => !kanaInput || unitAt[position] >= 0 || position == length;

        // 知らない英字として英語にできる文字 (ローマ字として読めない英字・大文字・打ち終わって読めない入力途中の子音) の数の累積
        var unknown = new int[length + 1];
        // 入力途中の子音は、入力中も Enter のときと同じ読みで重みを付ける (表示と確定の区切りを揃える)
        var finalPending = _romaji.ConvertLenient(pending.ToLowerInvariant(), final: true);
        for (var p = 0; p < length; p++)
        {
            var c = text[p];
            var unreadable = char.IsAsciiLetterUpper(c) ||
                (p < unitsEnd ? unitAt[p] is var k and >= 0 && prepared.Unreadable[k + 1] > prepared.Unreadable[k] : finalPending.Any(char.IsAsciiLetter));
            unknown[p + 1] = unknown[p] + (char.IsAsciiLetter(c) && unreadable ? 1 : 0);
        }

        // ltu・xtu・ltsu・xtsu で打った っ の数を、辺ごとに数え直さず位置の累積で引く。
        // 入力の最後で終わる促音は、後ろの文字が打たれるまで重みに数えない。
        if (_sokuonLen.Length < length)
        {
            var capacity = Math.Max(length, _sokuonLen.Length * 2);
            Array.Resize(ref _sokuonLen, capacity);
            Array.Resize(ref _sokuonPrefix, capacity);
        }
        var sokuonLen = _sokuonLen;
        var sokuonPrefix = _sokuonPrefix;
        for (var i = 0; i + 2 < length; i++)
        {
            var match = (byte)0;
            if (char.ToLowerInvariant(text[i]) is 'l' or 'x' && char.ToLowerInvariant(text[i + 1]) == 't')
            {
                if (char.ToLowerInvariant(text[i + 2]) == 's')
                {
                    if (i + 4 < length && char.ToLowerInvariant(text[i + 3]) == 'u') match = 4;
                }
                else if (char.ToLowerInvariant(text[i + 2]) == 'u' && i + 3 < length) match = 3;
            }
            sokuonLen[i] = match;
            sokuonPrefix[i + 1] = sokuonPrefix[i] + (match > 0 ? 1 : 0);
        }

        // [位置, 状態, 軽い順]。ビット集合で到達した状態だけを番号順にたどる (同じコストのときの順番を保つ)。
        var stride = LatticeState.Count * keep;
        var needed = (length + 1) * stride;
        if (_best.Length < needed)
        {
            var capacity = Math.Max(needed, _best.Length * 2);
            Array.Resize(ref _best, capacity);
            Array.Resize(ref _bestStamp, capacity);
        }
        var best = _best;
        var stamp = _bestStamp;
        // 0 は未使用の印なので、長く使い続けて番号が一周したら表を消す。
        if (++_bestGeneration == 0)
        {
            Array.Clear(stamp);
            _bestGeneration = 1;
        }
        var gen = _bestGeneration;
        if (_reached.Length <= length) _reached = new UInt128[Math.Max(length + 1, _reached.Length * 2)];
        else Array.Clear(_reached, 0, length + 1);
        var reached = _reached;
        var start = new LatticeState(precedingEnglish switch { true => englishSentence ? Side.Sentence : Side.English, false => Side.Japanese, null => Side.None },
            false, true, precedingEnglish == true, ContextSwitch: precedingEnglish == true);
        // 理由を残すときは、あとで評価する関数がノードを見るので新しい配列を使う。残さないときは前の配列を頭から使い回す
        var nodes = reasons is null ? _latticeNodes : new LatticeNode[64];
        var nodeCount = 0;
        void EnsureNodes()
        {
            if (nodeCount < nodes.Length) return;
            var size = Math.Max(64, nodes.Length * 2);
            Array.Resize(ref nodes, size);
            if (reasons is null) _latticeNodes = nodes;
        }
        EnsureNodes();
        nodes[nodeCount++] = new LatticeNode(0, -1, default);
        best[start.Key * keep] = 0;
        stamp[start.Key * keep] = gen;
        reached[0] = UInt128.One << start.Key;

        // 特徴の重みは、辺をつなぐたびに表を引かないよう、外側の変数に取っておく
        var switchWeight = Weights[LinkFeature.Switch];
        var sentenceSwitchWeight = Weights[LinkFeature.SentenceSwitch];
        var adjacentEnglishWeight = Weights[LinkFeature.AdjacentEnglish];
        var sandwichWeight = Weights[LinkFeature.Sandwich];
        var nameSplitWeight = Weights[LinkFeature.NameSplit];
        var spelledSokuonWeight = Weights[LinkFeature.SpelledSokuon];
        var vowelAfterEnglishWeight = Weights[LinkFeature.VowelAfterEnglish];
        var danglingSokuonWeight = Weights[LinkFeature.DanglingSokuon];

        void Offer(int previous, LatticeEdge edge, int key, double cost)
        {
            var offset = edge.End * stride + key * keep;
            if (keep == 1)
            {
                // 1 つだけ残すときは、同じ位置・状態の道を上書きする。edge.End > p なので、この道を Prev に持つ道はまだ無い
                if (stamp[offset] != gen)
                {
                    EnsureNodes();
                    nodes[nodeCount] = BuildNode(previous, edge, cost);
                    best[offset] = nodeCount++;
                    stamp[offset] = gen;
                    reached[edge.End] |= UInt128.One << key;
                    return;
                }
                var current = best[offset];
                if (!(cost < nodes[current].Cost)) return;
                nodes[current] = BuildNode(previous, edge, cost);
                return;
            }
            var at = keep;
            while (at > 0 && (stamp[offset + at - 1] != gen || cost < nodes[best[offset + at - 1]].Cost)) at--;
            if (at == keep) return;
            for (var k = keep - 1; k > at; k--)
            {
                best[offset + k] = best[offset + k - 1];
                stamp[offset + k] = stamp[offset + k - 1];
            }
            // 比較で落ちる道のノードは作らず、上位に残す道だけを確保する。
            EnsureNodes();
            nodes[nodeCount] = BuildNode(previous, edge, cost);
            best[offset + at] = nodeCount++;
            stamp[offset + at] = gen;
            reached[edge.End] |= UInt128.One << key;
        }

        LatticeNode BuildNode(int previous, LatticeEdge edge, double cost)
        {
            // 英語に続く日本語に助詞がないかを覚えて、次の英語の辺で経路をさかのぼらずに済むようにする。
            var parent = nodes[previous];
            var adjacentEnglish = parent.Edge.English && parent.Edge.End == edge.Start && edge.Start > 0 &&
                char.IsAsciiLetter(text[edge.Start - 1]);
            var sandwiched = !edge.Particle && (adjacentEnglish ||
                !edge.English && !edge.Neutral && !parent.Edge.English && !parent.Edge.Neutral && parent.Sandwiched);
            return new LatticeNode(cost, previous, edge, sandwiched);
        }

        double LinkCostFor(in LatticeNode from, LatticeState state, in LinkEdge link)
        {
            var edge = link.Edge;
            var switches = 0;
            var sentenceSwitches = 0;
            bool dangling;
            var cost = 0.0;
            if (!edge.English && !edge.Neutral)
            {
                if (state.Side == Side.Sentence) sentenceSwitches++;
                // 3 文字以上の英単語の後ろでは、助詞から始まる語 (java|のこ) と助詞 (java|の) を同じ重みで扱う。
                if (state.Side == Side.English && !edge.Particle &&
                    !(link.NounParticleStart && from.Edge.English && from.Edge.End - from.Edge.Start >= 3)) switches++;
                if (state.ContextSwitch && edge.Start > 0)
                {
                    if (englishSentence) sentenceSwitches++;
                    else switches++;
                }
                dangling = link.DanglingSokuon && state.Side != Side.Japanese;
            }
            else
            {
                if (edge.English && state.Side == Side.Japanese && !state.Particle) switches++;
                dangling = state.Sokuon;
            }

            // 足し算の丸めで経路の順位が変わらないよう、重みは LinkFeature の順に足す。
            if (switches > 0) cost += switches * switchWeight;
            if (sentenceSwitches > 0) cost += sentenceSwitches * sentenceSwitchWeight;
            if (edge.English && link.BetweenLetters)
            {
                if (state.IsEnglish) cost += adjacentEnglishWeight;
                if (state.Side == Side.Japanese && from.Sandwiched) cost += sandwichWeight;
            }
            if (link.NameSplit && from.Edge.Name && from.Edge.End == edge.Start) cost += nameSplitWeight;
            if (link.SpelledSokuons > 0) cost += link.SpelledSokuons * spelledSokuonWeight;
            if (state.PreviousEnglish && link.VowelAfter) cost += vowelAfterEnglishWeight;
            if (dangling) cost += danglingSokuonWeight;
            return cost;
        }

        void OfferLink(int from, LatticeState state, in LinkEdge link, bool letterAtStart)
        {
            var edge = link.Edge;
            // 活用語に付く 1 文字 (う・れ) が英語の直後や入力の頭に来たら、語にならないかなとして数える。
            var cost = link.VerbAuxiliary && (state.IsEnglish || state.Side == Side.None && edge.End < length)
                ? UnknownKanaCost : link.Cost;
            if (edge.English) cost += EnglishTieBreak + englishBias;
            var next = edge.English
                // 英文の文脈は最初の英単語まで引き継ぐ。
                ? new LatticeState(state.Side == Side.Sentence && !letterAtStart ? Side.Sentence : Side.English, false, state.AllEnglish, true, ContextSwitch: state.ContextSwitch)
                : edge.Neutral ? state with { PreviousEnglish = false, Sokuon = false }
                : new LatticeState(Side.Japanese, edge.Particle, state.AllEnglish && !edge.Letters, false, Sokuon: link.Sokuon);
            Offer(from, edge, next.Key, nodes[from].Cost + cost + LinkCostFor(nodes[from], state, link));
        }

        var englishLevel = !manual;

        if (englishLevel && !kanaInput) PrepareLexemeRuns(text, units);
        var context = new ReadingContext(Memory, Memory?.Version ?? -1, learning, precedingEnglish == true, kanaInput, manual);
        BeginReading(text, units, prepared, context);

        // 記号・数字の決まりはどの道から来ても同じなので、位置ごとに 1 回だけ調べる。
        Lexeme Lex(int p)
        {
            var unit = p < unitsEnd ? unitAt[p] : -1;
            // 技術用語の綴り + する の活用 (share|して・make|します): 日本語の語 (しゃれ・まけ) には する を付けられないので、日本語の候補を出さない
            var technical = !kanaInput && englishLevel && IsTechnicalWordBeforeSuru(text, p, _letterRunEnd[p]);
            // ローマ字として読める語 + . + ドメイン・拡張子 (llama.cpp・hoge.io): 句点の後ろの日本語 (ryoukaidesu.|deha) と重みで比べる
            var domain = unit >= 0 && !kanaInput && englishLevel ? ReadableStemDotSuffixEnd(prepared, learning, units, unit, pending) : -1;
            if (unit < 0 || kanaInput) return new(LexemeRule.None, -1, domain, technical);
            // 上から順に、先に当てはまった決まりを使う
            if (LeadingDotfileEnd(prepared, learning, units, unit, pending, manual) is >= 0 and var dotfile) return new(LexemeRule.Dotfile, dotfile, domain, technical);
            if (UserNameEnd(prepared, units, unit, pending) is >= 0 and var user) return new(LexemeRule.UserName, user, domain, technical);
            if (!englishLevel) return new(LexemeRule.None, -1, domain, technical);
            // 英字の連なりの後ろがハイフンや数字のときだけ、対応する字句の規則を調べる。
            var lettersEnd = _unitLetterRunEnd[unit];
            if (lettersEnd > unit && lettersEnd < units.Count)
            {
                var next = units[lettersEnd].Raw;
                if (next == "-" &&
                    HyphenatedWordEnd(prepared, learning, units, unit, pending) is >= 0 and var hyphenated) return new(LexemeRule.HyphenatedWord, hyphenated, domain, technical);
                if (next is [var digit] && char.IsAsciiDigit(digit) &&
                    NumeronymEnd(prepared, units, unit, pending) is >= 0 and var numeronym) return new(LexemeRule.Numeronym, numeronym, domain, technical);
            }
            var partEnd = _unitPartRunEnd[unit];
            if ((_unitDigitsAfter[unit] > _unitDigitsAfter[partEnd] ||
                 partEnd == units.Count && pending.AsSpan().ContainsAnyInRange('0', '9')) &&
                ProperNounWithDigitsEnd(prepared, units, unit, pending) is >= 0 and var proper) return new(LexemeRule.ProperNounWithDigits, proper, domain, technical);
            if (units[unit].Raw == "-" &&
                VersionSuffixEnd(units, unit, pending) is >= 0 and var version) return new(LexemeRule.VersionSuffix, version, domain, technical);
            return new(LexemeRule.None, -1, domain, technical);
        }
        var lexemes = LatticeTrace is null ? null : new Lexeme[length];
        if (lexemes is not null)
        {
            for (var p = 0; p < length; p++)
                if (IsBoundary(p)) lexemes[p] = Lex(p);
        }

        for (var p = 0; p < length; p++)
        {
            if (reached[p] == 0) continue;
            if (!IsBoundary(p)) continue;
            var japanese = JapaneseEdges(prepared, units, text, p, unitOf, unitsEnd, pending, kanaInput);
            var english = EnglishEdges(prepared, learning, text, p, unknown, kanaInput, manual, IsBoundary, precedingEnglish == true);
            var unit = p < unitsEnd ? unitAt[p] : -1;
            var letterAtP = char.IsAsciiLetter(text[p]);
            var previousLower = p > 0 && char.IsAsciiLetterLower(text[p - 1]);
            var betweenLetters = p > 0 && char.IsAsciiLetter(text[p - 1]) && letterAtP;

            List<LinkEdge> ReadLinks(EdgeCache cache, List<(LatticeEdge Edge, double Cost)> edges)
            {
                if (cache.Generation != 0) return cache.Links;
                cache.Links.Clear();
                foreach (var (edge, cost) in edges)
                {
                    cache.Links.Add(MakeLink(text, length, p, edge, cost, previousLower, betweenLetters, sokuonLen, sokuonPrefix));
                    // 促音や c 行は、辺の終わりに続く文字で読み方が変わる。
                    cache.ReadTo = Math.Max(cache.ReadTo, edge.End + 1);
                }
                cache.Generation = _readingGeneration;
                return cache.Links;
            }

            var linksJ = ReadLinks(_japaneseEdges[p]!, japanese);
            var linksE = ReadLinks(_englishEdges[p]!, english);
            // 記号・数字・空白・z の記号 (zh = ←) の単位: 言語を切り替えない
            LatticeEdge? neutral = unit >= 0 && !IsKanaUnit(units[unit]) && !IsLetterUnit(units[unit])
                ? new LatticeEdge(p, prepared.RawOffsets[unit + 1], false, units[unit].Kana, false, false, true)
                : null;
            var (_, forced, domain, technical) = lexemes is null ? Lex(p) : lexemes[p];

            // . の後ろのドメイン・拡張子 (tetr.io、MeltypeTip.dll) は、前が英語の道のときだけ区切る
            var dotSuffixPossible = forced < 0 && unit > 0 && englishLevel && !kanaInput && units[unit - 1].Raw == ".";

            var forcedLink = forced > unit
                ? MakeLink(text, length, p, new LatticeEdge(p, forced == units.Count ? length : prepared.RawOffsets[forced], true, "", false, false), KnownEnglishCost, previousLower, betweenLetters, sokuonLen, sokuonPrefix)
                : (LinkEdge?)null;
            var domainLink = domain > unit && !technical
                ? MakeLink(text, length, p, new LatticeEdge(p, domain == units.Count ? length : prepared.RawOffsets[domain], true, "", false, false),
                    KnownEnglishCost + ReadableDomainLetterCost * (text.IndexOf('.', p) - p), previousLower, betweenLetters, sokuonLen, sokuonPrefix)
                : (LinkEdge?)null;
            var symbolLink = unit >= 0 && !kanaInput && IsAsciiSymbol(units[unit])
                ? MakeLink(text, length, p, new LatticeEdge(p, p + 1, true, "", false, false), 0, previousLower, betweenLetters, sokuonLen, sokuonPrefix)
                : (LinkEdge?)null;
            var neutralLink = neutral is { } symbolUnit ? MakeLink(text, length, p, symbolUnit, 0, previousLower, betweenLetters, sokuonLen, sokuonPrefix) : (LinkEdge?)null;

            // 記号を半角にするかと . の後ろが拡張子かは、条件を満たす道が初めて来たときに調べる。
            bool? keepsPunct = null;
            var dotEnd = -2;
            LinkEdge? dotLink = null;
            var spanJ = CollectionsMarshal.AsSpan(linksJ);
            var spanE = CollectionsMarshal.AsSpan(linksE);
            for (var remaining = reached[p]; remaining != 0; remaining &= remaining - 1)
            {
                var key = (int)UInt128.TrailingZeroCount(remaining);
                var state = LatticeState.FromKey(key);
                var offset = p * stride + key * keep;
                for (var slot = 0; slot < keep && stamp[offset + slot] == gen; slot++)
                {
                    var node = best[offset + slot];
                    var end = forced;
                    if (end < 0 && dotSuffixPossible && state.PreviousEnglish)
                        end = dotEnd != -2 ? dotEnd : (dotEnd = DotSuffixEnd(prepared, learning, units, unit, pending));
                    if (end > unit)
                    {
                        if (end != forced && dotLink is null)
                            dotLink = MakeLink(text, length, p, new LatticeEdge(p, end == units.Count ? length : prepared.RawOffsets[end], true, "", false, false),
                                KnownEnglishCost, previousLower, betweenLetters, sokuonLen, sokuonPrefix);
                        OfferLink(node, state, end == forced ? forcedLink!.Value : dotLink!.Value, letterAtP);
                        continue;
                    }
                    if (technical)
                    {
                        // 日本語の候補だけを出さない。英語の候補はそのまま使う (open は english-readable.txt の重み 0.7 のまま)
                        foreach (ref readonly var link in spanE) OfferLink(node, state, link, letterAtP);
                        continue;
                    }
                    if (domainLink is { } domainEdge) OfferLink(node, state, domainEdge, letterAtP);
                    // 英文の中の記号 (, . ! ? -) は半角のまま
                    if (symbolLink is { } symbol && state.PreviousEnglish && state.AllEnglish &&
                        (keepsPunct ??= KeepsPunctuationAfterEnglish(units, unit, pending, precedingEnglish)))
                    {
                        OfferLink(node, state, symbol, letterAtP);
                        continue;
                    }
                    if (neutralLink is { } neutralEdge) OfferLink(node, state, neutralEdge, letterAtP);
                    foreach (ref readonly var link in spanJ) OfferLink(node, state, link, letterAtP);
                    foreach (ref readonly var link in spanE) OfferLink(node, state, link, letterAtP);
                }
            }
        }

        var chosen = -1;
        var chosenCost = double.MaxValue;
        var trace = LatticeTrace;
        List<(double Cost, LatticeState State, int Node)>? finals = count > 1 || reasons is not null || trace is not null ? [] : null;
        for (var slot = 0; slot < LatticeState.Count * keep; slot++)
        {
            var key = slot / keep;
            if (stamp[length * stride + slot] != gen) continue;
            var node = best[length * stride + slot];
            var state = LatticeState.FromKey(key);
            var cost = nodes[node].Cost;
            if (state.Sokuon) cost += danglingSokuonWeight;
            if (followingEnglish == true && state.Side == Side.Japanese && !state.Particle) cost += switchWeight;
            if (followingEnglish == false && state.IsEnglish && nodes[node].Edge.English) cost += switchWeight;
            finals?.Add((cost, state, node));
            if (cost < chosenCost)
            {
                chosen = node;
                chosenCost = cost;
            }
        }
        if (trace is not null)
        {
            trace($"lattice {text} (manual {manual}, preceding {precedingEnglish?.ToString() ?? "-"}, following {followingEnglish?.ToString() ?? "-"}, bias {englishBias})");
            for (var p = 0; p < length; p++)
                if (lexemes?[p] is { Rule: not LexemeRule.None } lexeme) trace($"  字句 {p}: {lexeme.Rule} (単位 {lexeme.End} まで英語)");
            foreach (var (cost, state, node) in finals!.OrderBy(f => f.Cost))
                trace($"  {(node == chosen ? "*" : " ")} {cost:0.00} {state.Side}{(state.Particle ? " particle" : "")}{(state.Sokuon ? " sokuon" : "")}: {DescribePath(nodes, node, text, true)}");
        }
        if (reasons is not null && chosen >= 0) reasons.Add(() => Explain(nodes, chosen, chosenCost, finals!, text));
        var alternatives = new List<List<CompositionSegment>> { Path(chosen) };
        if (count <= 1) return alternatives;
        foreach (var (cost, _, node) in finals!.OrderBy(f => f.Cost))
        {
            if (alternatives.Count >= count || cost > chosenCost + AlternativeMargin) break;
            var segments = Path(node);
            if (!alternatives.Any(a => a.SequenceEqual(segments))) alternatives.Add(segments);
        }
        return alternatives;

        List<CompositionSegment> Path(int last)
        {
            var edges = new List<LatticeEdge>();
            for (var node = last; node >= 0 && nodes[node].Prev >= 0; node = nodes[node].Prev) edges.Add(nodes[node].Edge);
            edges.Reverse();
            return ToSegments(prepared, text, unitAt, unitsEnd, edges);
        }
    }

    private void BeginReading(string text, IReadOnlyList<CompositionUnit> units, Prepared prepared, ReadingContext context)
    {
        var size = Math.Max(_readingText.Length, text.Length) + 1;
        if (_readingChanged.Length < size)
        {
            var capacity = Math.Max(size, _readingChanged.Length * 2);
            Array.Resize(ref _readingChanged, capacity);
            Array.Resize(ref _japaneseEdges, capacity);
            Array.Resize(ref _englishEdges, capacity);
        }

        // 以前に候補を作った位置が今回の経路から外れていても、読んだ範囲の変更を次に検出できるようにする。
        var shared = 0;
        var unchangedUnits = 0;
        if (++_readingGeneration == 0)
        {
            Array.Clear(_japaneseEdges);
            Array.Clear(_englishEdges);
            _readingGeneration = 1;
        }
        else if (context == _readingContext)
        {
            var count = Math.Min(units.Count, _readingUnitCount);
            // 後ろの文字次第で w が笑いから読めない英字に変わるため、単位と一緒に英字の数も比較する。
            while (unchangedUnits < count && _readingUnits[unchangedUnits] == units[unchangedUnits] &&
                _readingUnreadable[unchangedUnits] == prepared.Unreadable[unchangedUnits + 1]) unchangedUnits++;
            shared = unchangedUnits == units.Count && unchangedUnits == _readingUnitCount && text == _readingText
                ? text.Length + 1 : prepared.RawOffsets[unchangedUnits];
        }
        // 位置がずれると後ろの候補も同じ添字では読めないため、最初の変更から末尾まで同じ番号を付ける。
        Array.Fill(_readingChanged, _readingGeneration, shared, size - shared);

        if (_readingUnits.Length < units.Count)
        {
            var capacity = Math.Max(units.Count, _readingUnits.Length * 2);
            Array.Resize(ref _readingUnits, capacity);
            Array.Resize(ref _readingUnreadable, capacity);
        }
        for (var i = unchangedUnits; i < units.Count; i++)
        {
            _readingUnits[i] = units[i];
            _readingUnreadable[i] = prepared.Unreadable[i + 1];
        }
        if (_readingUnitCount > units.Count) Array.Clear(_readingUnits, units.Count, _readingUnitCount - units.Count);
        _readingUnitCount = units.Count;
        _readingContext = context;
        _readingText = text;
    }

    private bool CanReuse(EdgeCache cache) => cache.Generation != 0 && _readingChanged[cache.ReadTo - 1] <= cache.Generation;

    private void PrepareLexemeRuns(string text, IReadOnlyList<CompositionUnit> units)
    {
        if (_letterRunEnd.Length <= text.Length) Array.Resize(ref _letterRunEnd, Math.Max(text.Length + 1, _letterRunEnd.Length * 2));
        if (_unitLetterRunEnd.Length <= units.Count)
        {
            var capacity = Math.Max(units.Count + 1, _unitLetterRunEnd.Length * 2);
            Array.Resize(ref _unitLetterRunEnd, capacity);
            Array.Resize(ref _unitPartRunEnd, capacity);
            Array.Resize(ref _unitDigitsAfter, capacity);
        }

        // 同じ英字の連なりを何度も調べずに済むよう、文字と単位の両方で連なりの末尾を求める。
        _letterRunEnd[text.Length] = text.Length;
        for (var i = text.Length - 1; i >= 0; i--)
            _letterRunEnd[i] = char.IsAsciiLetter(text[i]) ? _letterRunEnd[i + 1] : i;
        _unitLetterRunEnd[units.Count] = _unitPartRunEnd[units.Count] = units.Count;
        _unitDigitsAfter[units.Count] = 0;
        for (var i = units.Count - 1; i >= 0; i--)
        {
            var raw = units[i].Raw.AsSpan();
            var digits = raw.ContainsAnyInRange('0', '9');
            _unitLetterRunEnd[i] = raw.Length > 0 && units[i].Raw.All(char.IsAsciiLetter)
                ? _unitLetterRunEnd[i + 1] : i;
            _unitPartRunEnd[i] = raw.Length > 0 && units[i].Raw.All(char.IsAsciiLetterOrDigit) ? _unitPartRunEnd[i + 1] : i;
            _unitDigitsAfter[i] = _unitDigitsAfter[i + 1] + (digits ? 1 : 0);
        }
    }

    /// <summary>選んだ道と次点の道。次点は、文字ごとの言語 (英語 / 日本語) が選んだ道と違う道のうち一番軽いもの (同じ区切りで状態だけ違う道は比べても意味がない)。</summary>
    private static string Explain(LatticeNode[] nodes, int chosen, double chosenCost, List<(double Cost, LatticeState State, int Node)> finals, string text)
    {
        var languages = Languages(nodes, chosen, text.Length);
        var runnerCost = double.MaxValue;
        var runner = -1;
        foreach (var (cost, _, node) in finals)
            if (cost < runnerCost && Languages(nodes, node, text.Length) != languages) { runnerCost = cost; runner = node; }
        return $"区切り {DescribePath(nodes, chosen, text, Diagnostics.Log.RecordText)} 計 {chosenCost:0.00}" +
            (runner < 0 ? "、ほかの区切りなし" : $"、次点 {DescribePath(nodes, runner, text, Diagnostics.Log.RecordText)} 計 {runnerCost:0.00} (差 {runnerCost - chosenCost:0.00})");
    }

    /// <summary>
    /// 道の候補ごとに 言語:打った文字=読み+重み を並べる (EN:google+1 JA:kyouha=きょうは+2)。
    /// 打った文字を出さないなら、文字の代わりに文字数を出す (JA(6)+2)。
    /// </summary>
    private static string DescribePath(LatticeNode[] nodes, int node, string text, bool withText)
    {
        var path = new List<string>();
        for (var at = node; nodes[at].Prev is var previous && previous >= 0; at = previous)
        {
            var raw = text[nodes[at].Edge.Start..nodes[at].Edge.End];
            var label = nodes[at].Edge.English ? "EN" : nodes[at].Edge.Neutral ? "--" : "JA";
            var shown = withText ? $"{label}:{raw}{(nodes[at].Edge.Kana.Length > 0 && nodes[at].Edge.Kana != raw ? "=" + nodes[at].Edge.Kana : "")}" : $"{label}({raw.Length})";
            path.Add($"{shown}+{nodes[at].Cost - nodes[previous].Cost:0.##}");
        }
        path.Reverse();
        return string.Join(" ", path);
    }

    /// <summary>道が文字ごとに英語 (E) か日本語 (J) か。記号・数字 (-) は言語を変えないので比べない。</summary>
    private static string Languages(LatticeNode[] nodes, int node, int length)
    {
        var languages = new char[length];
        for (var at = node; nodes[at].Prev >= 0; at = nodes[at].Prev)
            Array.Fill(languages, nodes[at].Edge.English ? 'E' : nodes[at].Edge.Neutral ? '-' : 'J', nodes[at].Edge.Start, nodes[at].Edge.End - nodes[at].Edge.Start);
        return new string(languages);
    }

    /// <summary>名詞に付く付属語か (が・を・に・で・する …)。英単語との切り替えの重みを 0 にする。</summary>
    private static bool IsNounParticle(string kana) => NounParticles.Contains(kana) || IsSuruForm(kana) || kana is "し" or "さ" or "せ";

    private static readonly HashSet<string> NounParticles = ["が", "を", "に", "で", "と", "の", "は", "も", "へ", "や", "から", "まで", "より", "って", "だ", "です"];

    /// <summary>
    /// 動詞・形容詞の活用形の後ろにしか付かない、1 文字の付属語 (名詞や英単語の後ろには付かない)。
    /// 2 文字以上の付属語 (ます・ません) は、英単語を切り出した残りにはまずならず、なっても日本語の根拠として十分なので入れない。
    /// </summary>
    private static bool IsVerbAuxiliary(string kana) => kana is "う" or "た" or "て" or "ば" or "れ";

    /// <summary>
    /// start から始まる技術用語の綴り (share・make) のすぐ後ろが する の活用 (して・します) か。
    /// その綴りに付属語でない日本語の読み (しゃれ・まけ) があり、日本語由来の語 (ninja) でなければ、
    /// その区間を日本語の語として読まない (issue #1899)。「しゃれする」「まけします」は日本語として成り立たないため。
    /// ただし させ は一段動詞の使役形 (負けさせた・褒めさせて) とも読めるので、読みが一段動詞の語幹なら日本語のまま。
    /// </summary>
    private bool IsTechnicalWordBeforeSuru(string text, int start, int runEnd)
    {
        for (var end = start + 1; end < runEnd && end - start <= MaxSpan; end++)
        {
            if (!char.IsAsciiLetterLower(text[end - 1])) break;
            var word = text.AsSpan(start, end - start);
            // 日本語由来の語 (ninja) は、日本語の読み (にんじゃ) のまま使うことがある。
            if (!TechnicalEnglishWords.Value.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word) ||
                JapaneseOriginWords.Value.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word)) continue;
            // 日本語の読みがある語だけ (github のように読めない語は、日本語の道がもともと重い)
            if (_romaji.ConvertLenient(word.ToString(), final: true) is not { Length: > 0 } reading || !reading.All(KanaChars.IsHiraganaOrLongMark) ||
                RomajiTypoCorrector.IsFunctionWord(reading)) continue;
            // すぐ後ろが する の活用か (して・します・した …)。し だけは日本語の語の終わり (恩師・分子・如し) と紛れるので除く
            if (_romaji.ConvertLenient(text[end..runEnd].ToLowerInvariant(), final: true) is var next && next.All(KanaChars.IsHiraganaOrLongMark) &&
                IsSuruForm(next))
            {
                // 一段動詞の使役形 (負けさせた・褒めさせて) は する の使役形と同じかな。日本語の候補を残す
                if (next.StartsWith("させ", StringComparison.Ordinal) && CommonReadings.IchidanStems.Contains(reading)) continue;
                return true;
            }
        }
        return false;
    }

    /// <summary>position の英字が、次の文字と同じ子音を重ねたもの (tt の t = っ)。</summary>
    private static bool DoubledAt(string text, int length, int position) =>
        position + 1 < length && char.IsAsciiLetter(text[position]) && char.ToLowerInvariant(text[position + 1]) == char.ToLowerInvariant(text[position]);

    /// <summary>
    /// 辺に、状態によらずに決まる値 (かなの頭の母音・付属語か・っ の重み・打った っ の数) を付ける。位置 p の辺ごとに 1 回だけ呼ぶ。
    /// </summary>
    private static LinkEdge MakeLink(string text, int length, int p, LatticeEdge edge, double cost, bool previousLower, bool betweenLetters,
        byte[] sokuonLen, int[] sokuonPrefix)
    {
        if (edge.English || edge.Neutral) return new LinkEdge(edge, cost, betweenLetters, false, false, false, false, false, false, 0);
        var kana = edge.Kana;
        var head = kana.Length > 0 ? kana[0] : '\0';
        // 小さい あ行は、前がどの文字でも切り間違いとして数える。大きい あ行は、前が子音のときだけ (issue|ある は除く)
        var previousNotVowel = previousLower && text[p - 1] is not ('a' or 'e' or 'i' or 'o' or 'u');
        return new LinkEdge(edge, cost, betweenLetters,
            VowelAfter: previousLower && (head is 'ぁ' or 'ぃ' or 'ぅ' or 'ぇ' or 'ぉ' || (head is 'あ' or 'い' or 'う' or 'え' or 'お') && previousNotVowel),
            NameSplit: !edge.Particle && char.IsAsciiLetterLower(text[p]),
            Sokuon: kana.Length > 0 && kana[^1] == 'っ' && DoubledAt(text, length, edge.End - 1),
            DanglingSokuon: kana == "っ" && DoubledAt(text, length, p),
            NounParticleStart: kana.Length > 1 && NounParticles.GetAlternateLookup<ReadOnlySpan<char>>().Contains(kana.AsSpan(0, 1)),
            VerbAuxiliary: IsVerbAuxiliary(kana),
            // [p, end) の中の一致 = [p, end-3] に始まる一致のうち終わりが end までのもの。
            // 長さ 3 の一致は終わりが end のものだけ残るので、end-3 の長さ 4 を引く
            SpelledSokuons: edge.End - 2 > p ? sokuonPrefix[edge.End - 2] - sokuonPrefix[p] - (sokuonLen[edge.End - 3] == 4 ? 1 : 0) : 0);
    }

    /// <summary>かなを、語にならないかなとして読むときの重み (1 音 3、っ・ー は 0.5。小書きのかな ゃ・ぃ は前のかなと 1 音)。</summary>
    private static double UnknownKana(string kana) =>
        kana.Select((c, i) => c is 'っ' or 'ー' ? LightKanaCost : i > 0 && KanaChars.IsSmallKana(c) ? 0 : UnknownKanaCost).Sum();

    /// <summary>単位の読みがかな (ひらがな・ー) か。</summary>
    private static bool IsKanaUnit(CompositionUnit unit) => unit.Kana.Length > 0 && unit.Kana.All(KanaChars.IsHiraganaOrLongMark);

    /// <summary>ローマ字として読めなかった英字の単位か (zoom の m)。</summary>
    private static bool IsLetterUnit(CompositionUnit unit) => unit.Kana == unit.Raw && unit.Raw.Length > 0 && unit.Raw.All(char.IsAsciiLetter);

    /// <summary>[start, end) の中の、c 行の綴り (ca・ci・cu・ce・co。ch は除く) の数。</summary>
    private static int CRowSpellings(string text, int start, int end)
    {
        var count = 0;
        for (var i = start; i < end; i++)
            if (text[i] == 'c' && (i + 1 >= text.Length || text[i + 1] != 'h')) count++;
        return count;
    }

    private (LatticeEdge Edge, double Cost) JapaneseEdge(int start, int end, string kana)
    {
        var particle = IsNounParticle(kana);
        if (RomajiTypoCorrector.CommonWordCost(kana) is { } cost) return (new LatticeEdge(start, end, false, kana, true, particle), cost);
        if (CommonReadings.Phrases.Contains(kana) || _userJapanese.Contains(kana)) return (new LatticeEdge(start, end, false, kana, true, false), JapaneseWordCost);
        if (CommonReadings.Katakana.Contains(kana) || CommonReadings.Brands.Contains(kana)) return (new LatticeEdge(start, end, false, kana, true, false), KatakanaWordCost);
        // readings-rare.txt の読みは katakana.txt とも重なる (おねしゃす) ので、ほかの一覧の軽い重みを先に使う
        if (RomajiTypoCorrector.IsRareWord(kana)) return (new LatticeEdge(start, end, false, kana, true, false), kana.Length < 5 ? ShortRareWordCost : RareWordCost);
        return (new LatticeEdge(start, end, false, kana, true, false), double.NaN);
    }

    /// <summary>
    /// start から始まる日本語の候補 (語・語にならないかな 1 音・読めない英字 1 文字)。読みは単位の読み (変換ボックスの表示と同じ) を使う。
    /// ローマ字入力では、英語の候補が単位の途中で終わった所 (medal|s|ha の ha) は、単位の残りをローマ字として読み直す。
    /// 日本語の語は単位の途中で終わってもよい (単位の頭をローマ字として読み直す)。大文字を含む単位は日本語にしない (かな入力を除く)。
    /// </summary>
    private List<(LatticeEdge, double)> JapaneseEdges(Prepared prepared, IReadOnlyList<CompositionUnit> units, string text, int start, int[] unitOf, int unitsEnd, string pending,
        bool kanaInput)
    {
        var cache = _japaneseEdges[start] ??= new();
        if (CanReuse(cache)) return cache.Candidates;
        var length = text.Length;
        var edges = cache.Start(start >= unitsEnd ? length + 1 : prepared.RawOffsets[unitOf[start] + 1]);
        var first = true;

        void Add(int end, string reading, bool wordOnly = false)
        {
            var (edge, cost) = JapaneseEdge(start, end, reading);
            if (first && !wordOnly)
            {
                // 1 音目は、語にならなくても読める (打っている途中の子音だけなら重み 0)
                // x で打った小書きの母音 (だめ|xe = ぇ) は伸ばす音なので ー と同じ
                var unknownCost = reading.Length == 0 ? 0 : reading is "ぁ" or "ぃ" or "ぅ" or "ぇ" or "ぉ" && text[start] == 'x' ? LightKanaCost : UnknownKana(reading);
                cost = double.IsNaN(cost) ? unknownCost : Math.Min(cost, unknownCost);
                first = false;
            }
            if (!double.IsNaN(cost)) edges.Add((edge, cost + CRowCost * CRowSpellings(text, start, end)));
        }

        bool LetterEdge(int at)
        {
            if (!char.IsAsciiLetterLower(text[at])) return false;
            // かなか ? ! の後ろで語の終わりまで続く w は笑い (きた|w・ほんとw？|w)。知らない英字と同じ重みで、日本語のまま置ける
            var laughter = at;
            while (laughter < length && text[laughter] == 'w') laughter++;
            cache.ReadTo = Math.Max(cache.ReadTo, laughter + 1);
            if (laughter > at && at > 0 && (char.IsAsciiLetterLower(text[at - 1]) || text[at - 1] is '?' or '!') &&
                (laughter == length || !char.IsAsciiLetter(text[laughter])))
                edges.Add((new LatticeEdge(at, laughter, false, text[at..laughter], true, false), UnknownLetterCost));
            edges.Add((new LatticeEdge(at, at + 1, false, text[at].ToString(), true, false), UnknownKanaCost));
            return true;
        }

        // 入力途中の子音 (pending) から始まる
        if (start >= unitsEnd)
        {
            // Shift を押して打った子音 (W) は日本語にしない
            if (text[start..].Any(char.IsAsciiLetterUpper)) return edges;
            if (_romaji.ConvertLenient(text[start..].ToLowerInvariant(), final: true) is var tail && tail.All(KanaChars.IsHiraganaOrLongMark)) Add(length, tail);
            else LetterEdge(start);
            return edges;
        }
        var u = unitOf[start];
        var unitStart = prepared.RawOffsets[u];
        var kana = "";
        if (start == unitStart)
        {
            if (IsLetterUnit(units[u]))
            {
                // 読めない英字 (zoom の m・grok の g) は、英字のまま日本語に残せる
                LetterEdge(start);
                return edges;
            }
        }
        else
        {
            // 単位の途中から: 残りをローマ字として読み直す。読み切れなければ、次の単位 (英字だけのもの) まで続けて読む
            // (bacon|n + i の ni、quiz|k + a の ka)
            Detection.RomajiAnalysis? reading = null;
            var next = u + 1;
            for (; !kanaInput && next <= Math.Min(units.Count, u + 3); next++)
            {
                if (next > u + 1 && units[next - 1].Raw is var raw && (raw.Length == 0 || !raw.All(char.IsAsciiLetterLower))) break;
                var rest = text[start..prepared.RawOffsets[next]];
                if (rest.Any(char.IsAsciiLetterUpper)) break;
                if (_romaji.AnalyzeFragment(rest) is { IsValid: true, Partial: "" } complete)
                {
                    reading = complete;
                    break;
                }
            }
            // 単位の末尾で読み切れなかったときは、次に綴りが足されれば読みが変わる。
            cache.ReadTo = Math.Max(cache.ReadTo, next > units.Count ? length + 1 : prepared.RawOffsets[next]);
            if (reading is null)
            {
                LetterEdge(start);
                return edges;
            }
            kana = reading.Kana;
            Add(prepared.RawOffsets[next], kana);
            if (next == units.Count) AddPending();
            u = next;
        }
        for (var k = u; k < units.Count; k++)
        {
            // 語の頭でなくなったら、それより長い語は無い (1 音目は語にならなくても読む)
            if (!first && !RomajiTypoCorrector.CommonHasPrefix(kana) && !RomajiTypoCorrector.RareHasPrefix(kana) && !CommonReadings.Katakana.HasPrefix(kana) && !CommonReadings.Brands.HasPrefix(kana) &&
                !CommonReadings.Phrases.HasPrefix(kana) && !_userJapanesePrefixes.Contains(kana)) break;
            var unit = units[k];
            cache.ReadTo = Math.Max(cache.ReadTo, prepared.RawOffsets[k + 1]);
            if (!IsKanaUnit(unit) || !kanaInput && unit.Raw.Any(char.IsAsciiLetterUpper)) break;
            var from = prepared.RawOffsets[k];
            // 語が単位の途中で終わる (日本語の後ろの英語が単位の途中から始まる)
            for (var end = from + 1; !kanaInput && !first && end < prepared.RawOffsets[k + 1]; end++)
                if (_romaji.AnalyzeFragment(text[from..end].ToLowerInvariant()) is { IsValid: true, Partial: "" } head) Add(end, kana + head.Kana, wordOnly: true);
            kana += unit.Kana;
            Add(prepared.RawOffsets[k + 1], kana);
            if (k + 1 == units.Count) AddPending();
        }
        return edges;

        // 入力の最後まで: 後ろの入力途中の子音 (pending) と、する の活用を打っている途中 (commit|す・test|す|r。名詞に付く付属語)
        void AddPending()
        {
            cache.ReadTo = length + 1;
            if (unitsEnd == length)
            {
                if (kana.Length > 0 && CompositionDetector.SuruForms.Any(form => form.Length > kana.Length && form.StartsWith(kana, StringComparison.Ordinal)))
                    edges.Add((new LatticeEdge(start, length, false, kana, true, true), JapaneseWordCost));
                return;
            }
            if (pending.Any(char.IsAsciiLetterUpper)) return;
            if (_romaji.ConvertLenient(pending.ToLowerInvariant(), final: true) is var tail && tail.All(KanaChars.IsHiraganaOrLongMark)) Add(length, kana + tail);
            else if (IsPendingSuruForm(kana, pending)) edges.Add((new LatticeEdge(start, length, false, kana, true, true), JapaneseWordCost));
        }
    }

    /// <summary>kana の後ろに入力途中の子音 (す|r・し|t) を打っている所で、母音を足すと する の活用になるか。</summary>
    private bool IsPendingSuruForm(string kana, string pending) =>
        pending.All(char.IsAsciiLetterLower) &&
        "aiueo".Any(vowel => _romaji.ConvertLenient(pending + vowel, final: true) is var next && CompositionDetector.IsSuruForm(kana + next));

    /// <summary>
    /// start から始まる、綴りで決まる候補: 英語 (知っている英単語・知らない英字・短縮形) と、
    /// ユーザーがかなに直して覚えた語 (日本語の語として重み 1)。
    /// </summary>
    private List<(LatticeEdge, double)> EnglishEdges(Prepared prepared, bool? learning, string text, int start, int[] unknown, bool kanaInput, bool manual, Func<int, bool> isBoundary, bool afterEnglish)
    {
        var cache = _englishEdges[start] ??= new();
        if (CanReuse(cache)) return cache.Candidates;
        var edges = cache.Start(start + 1);
        if (!char.IsAsciiLetter(text[start])) return edges;
        // 自動判定を使わないときは、Shift を押して打った大文字で始まる語だけ
        if (manual && !char.IsAsciiLetterUpper(text[start])) return edges;
        var length = text.Length;
        for (var end = start + 1; end <= length && end - start <= MaxSpan; end++)
        {
            var c = text[end - 1];
            if (!char.IsAsciiLetter(c) && c != '\'') break;
            // 1 文字の語と入力末尾の英単語の頭は、直後の文字や入力が続くかで候補になるかが変わる。
            cache.ReadTo = end + 1;
            if (!isBoundary(end)) continue;
            var span = text[start..end];
            double cost;
            if (span.Contains('\''))
            {
                if (!IsContraction(learning, span)) continue;
                cost = KnownEnglishCost;
            }
            else if (IsWordCasing(span) && IsLatticeEnglishWord(learning, span.ToLowerInvariant()) && (span.Length > 1 || Standalone(text, start, end, afterEnglish)) &&
                     !IsLowercaseJapaneseProperNoun(learning, span))
            {
                cost = Memory?.Get(span.ToLowerInvariant(), learning) == true ? LearnedEnglishCost
                    : (ReadableEnglish.Value.ContainsWord(span.ToLowerInvariant()) ? ReadableEnglishCost : KnownEnglishCost) + (span.Length <= 2 && span.All(char.IsAsciiLetterLower) ? ShortEnglishCost : 0);
                if (kanaInput && span.Length >= 4) cost += KanaInputEnglishBias;
                // 日本語由来の語 (suzuki・sushi) は、ローマ字で読んだかなも日本語の語として並べる
                if (span.All(char.IsAsciiLetterLower) && IsJapaneseOriginWord(span) &&
                    _romaji.ConvertLenient(span, final: true) is var reading && reading.All(KanaChars.IsHiraganaOrLongMark))
                    edges.Add((new LatticeEdge(start, end, false, reading, true, false), JapaneseWordCost));
            }
            else if (Memory?.Get(span.ToLowerInvariant(), learning) == false)
            {
                // かなに直して覚えた語 (google を ごおgぇ に) は、日本語の語として読む
                edges.Add((new LatticeEdge(start, end, false, _romaji.ConvertLenient(span.ToLowerInvariant(), final: true), true, false), JapaneseWordCost));
                continue;
            }
            else if (!kanaInput && end == length && span.Length >= 4 && span.All(char.IsAsciiLetterLower) &&
                     unknown[Math.Max(start, Math.Min(end, prepared.Raw.Length))] > unknown[start] && _english.IsPrefix(span))
                // 入力の最後の、英単語の頭 (insta|ll)。入力途中の子音より前に、ローマ字で読めない英字があるときだけ (mala は まぁ、mits は みつ の途中)
                cost = EnglishPrefixCost;
            else if (!kanaInput && unknown[end] > unknown[start])
            {
                var name = span.Length >= 3 && char.IsAsciiLetterUpper(span[0]) && char.IsAsciiLetterLower(span[1]);
                // 日本人名 (Takahashi) は長さによらず知っている英単語と同じ重みにする。小文字 1 文字ごとの重みでは、長い名前ほど頭で切って日本語を続けた道 (T|赤橋・Toyo|だ) が軽くなる (#1744)。
                // 名前の頭で切る道 (Ai|かわ) には、知らない語と同じく NameSplit の重みを足すので、Name の印は残す
                cost = name && IsJapaneseName(span) ? KnownEnglishCost
                    : span.Sum(ch => char.IsAsciiLetterUpper(ch) ? UnknownUpperCost : name ? NameLetterCost : UnknownLetterCost);
                edges.Add((new LatticeEdge(start, end, true, "", false, false, Name: name), cost));
                continue;
            }
            else continue;
            edges.Add((new LatticeEdge(start, end, true, "", false, false), cost));
        }
        return edges;
    }

    /// <summary>大文字 1 文字の後ろが小文字だけで、ローマ字で読むと日本人の姓・名の読み (dictionaries/names.txt) になるか (Takahashi・Sato)。</summary>
    private bool IsJapaneseName(string span) =>
        span.Skip(1).All(char.IsAsciiLetterLower) && _romaji.ConvertLenient(span.ToLowerInvariant(), final: true) is var reading &&
        reading.All(KanaChars.IsHiragana) && CommonReadings.Names.Contains(CommonReadings.WithoutLongVowels(reading));

    /// <summary>英単語の書き方の大文字・小文字か (its・Its・ITS と、固有名詞の書き方 GitHub)。iTs は its の書き方ではない。</summary>
    private bool IsWordCasing(string span) =>
        !span.Skip(1).Any(char.IsAsciiLetterUpper) || span.All(char.IsAsciiLetterUpper) || _proper.Canonical(span.ToLowerInvariant()) == span;

    /// <summary>
    /// 小文字で打った、japanese.txt の語と同じ綴りの固有名詞か (wakatte は わかって、Wakatte.TV は固有名詞)。
    /// 英単語の辞書には固有名詞の小文字も入っているので、固有名詞で見分ける。
    /// </summary>
    private bool IsLowercaseJapaneseProperNoun(bool? learning, string span) =>
        span.All(char.IsAsciiLetterLower) && Memory?.Get(span, learning) is null && _proper.Contains(span) &&
        _kana?.IsJapaneseWord(_romaji.ConvertLenient(span, final: true)) == true;

    /// <summary>
    /// [start, end) の前後が英字以外で区切られているか (1 文字の英単語 a・i は、区切ったときだけ語)。
    /// 入力の頭は、前が英語のときだけ区切りとして数える (a だけを打ったら あ)。
    /// </summary>
    private static bool Standalone(string text, int start, int end, bool afterEnglish) =>
        (start == 0 ? afterEnglish : !char.IsAsciiLetter(text[start - 1])) && (end == text.Length || !char.IsAsciiLetter(text[end]));

    /// <summary>
    /// 知っている英単語か (同梱の辞書・固有名詞・よく使う英単語の一覧・english-readable.txt・-pedia・ユーザーが英字に直して覚えた語)。
    /// 1 文字は a・i と、チャットの略し方の u (you)・r (are) だけ。
    /// </summary>
    /// <remarks>
    /// english.txt の語の -s の形 (deploys) も語にする。english-words.txt は deploy を持たず ploys を持つので、deploys を de|ploys に割ってしまう。
    /// </remarks>
    private bool IsLatticeEnglishWord(bool? learning, string lower)
    {
        if (Memory?.Get(lower, learning) is { } learned) return learned;
        if (lower.Length == 1) return lower is "a" or "i" or "u" or "r";
        return _english.Words.ContainsWord(lower) || _proper.Contains(lower) || IsCommonEnglishWord(lower) || ReadableEnglish.Value.ContainsWord(lower) ||
            lower.Length >= 4 && lower[^1] == 's' && _english.Words.ContainsWord(lower[..^1]) ||
            IsPediaEnglishWord(lower);
    }

    /// <summary>選んだ候補の並びを、英語・日本語の区間の並びにする (続いた日本語の候補は 1 つの区間にする。英語の候補は 1 つずつの区間)。</summary>
    private List<CompositionSegment> ToSegments(Prepared prepared, string text, int[] unitAt, int unitsEnd, List<LatticeEdge> edges)
    {
        var segments = new List<CompositionSegment>();
        foreach (var edge in edges)
        {
            var raw = text[edge.Start..edge.End];
            var kana = edge.English ? "" : EdgeKana(prepared, text, unitAt, unitsEnd, edge);
            if (segments.Count > 0 && !edge.English && !segments[^1].IsEnglish)
                segments[^1] = segments[^1] with { Kana = segments[^1].Kana + kana, Raw = segments[^1].Raw + raw };
            else segments.Add(new CompositionSegment(edge.English, kana, raw));
        }
        return segments.Count > 0 ? segments : [new CompositionSegment(false, "", text)];
    }

    /// <summary>
    /// 日本語の候補の表示の読み。単位の境目どうしなら単位の読み、単位の途中で区切ったなら候補の読み。
    /// 入力途中の子音 (pending) は表示・変換のときに後ろに足すので含めない。
    /// </summary>
    private string EdgeKana(Prepared prepared, string text, int[] unitAt, int unitsEnd, LatticeEdge edge)
    {
        var end = Math.Min(edge.End, unitsEnd);
        if (edge.Start >= end) return "";
        if (unitAt[edge.Start] >= 0 && unitAt[end] >= 0) return Kana(prepared, unitAt[edge.Start], unitAt[end]);
        if (edge.End <= unitsEnd) return edge.Kana;
        return _romaji.ConvertLenient(text[edge.Start..end].ToLowerInvariant(), final: false).Replace('-', 'ー');
    }
}
