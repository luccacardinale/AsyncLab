using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Linq;

const int PBKDF2_ITERATIONS = 50_000;
const int HASH_BYTES = 32;
const string CSV_URL = "https://www.gov.br/receitafederal/dados/municipios.csv";
const string OUT_DIR_NAME = "mun_hash_por_uf";
const string DIFF_DIR_NAME = "diffs";

string FormatTempo(long ms)
{
    var ts = TimeSpan.FromMilliseconds(ms);
    return $"{ts.Minutes}m {ts.Seconds}s {ts.Milliseconds}ms";
}

var sw = Stopwatch.StartNew();

string baseDir = Directory.GetCurrentDirectory();
string tempCsvPath = Path.Combine(baseDir, "municipios.csv");
string newCsvPath = Path.Combine(baseDir, "municipios_novo.csv");
string outRoot = Path.Combine(baseDir, OUT_DIR_NAME);
string diffRoot = Path.Combine(baseDir, DIFF_DIR_NAME);

Directory.CreateDirectory(outRoot);
Directory.CreateDirectory(diffRoot);

Console.WriteLine("Baixando CSV de municípios (Receita Federal) ...");

using (var httpClient = new HttpClient())
{
    var bytes = await httpClient.GetByteArrayAsync(CSV_URL);
    await File.WriteAllBytesAsync(newCsvPath, bytes);
}

if (File.Exists(tempCsvPath))
{
    Console.WriteLine("Comparando arquivo local com novo arquivo baixado...");

    var linhasLocais = await File.ReadAllLinesAsync(tempCsvPath, Encoding.UTF8);
    var linhasNovas = await File.ReadAllLinesAsync(newCsvPath, Encoding.UTF8);

    var localSet = new HashSet<string>(linhasLocais);
    var novoSet = new HashSet<string>(linhasNovas);

    var adicionadas = novoSet.Except(localSet).ToList();
    var removidas = localSet.Except(novoSet).ToList();

    if (adicionadas.Count > 0 || removidas.Count > 0)
    {
        string diffPath = Path.Combine(
            diffRoot,
            $"diff_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        );

        var sb = new StringBuilder();

        foreach (var linha in adicionadas)
        {
            sb.AppendLine("[ADICIONADO]");
            sb.AppendLine(linha);
            sb.AppendLine();
        }

        foreach (var linha in removidas)
        {
            sb.AppendLine("[REMOVIDO]");
            sb.AppendLine(linha);
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(diffPath, sb.ToString(), Encoding.UTF8);

        Console.WriteLine($"Diferenças encontradas e salvas em: {diffPath}");
    }
    else
    {
        Console.WriteLine("Nenhuma diferença encontrada.");
    }

    File.Delete(tempCsvPath);
}

File.Move(newCsvPath, tempCsvPath);

Console.WriteLine("Lendo e parseando o CSV ...");

var linhas = await File.ReadAllLinesAsync(tempCsvPath, Encoding.UTF8);

if (linhas.Length == 0)
{
    Console.WriteLine("Arquivo CSV vazio.");
    return;
}

int startIndex = 0;

if (linhas[0].IndexOf("IBGE", StringComparison.OrdinalIgnoreCase) >= 0 ||
    linhas[0].IndexOf("UF", StringComparison.OrdinalIgnoreCase) >= 0)
{
    startIndex = 1;
}

var municipios = new List<Municipio>(linhas.Length - startIndex);

for (int i = startIndex; i < linhas.Length; i++)
{
    var linha = (linhas[i] ?? "").Trim();

    if (string.IsNullOrWhiteSpace(linha))
        continue;

    var parts = linha.Split(';');

    if (parts.Length < 5)
        continue;

    municipios.Add(new Municipio
    {
        Tom = Util.San(parts[0]),
        Ibge = Util.San(parts[1]),
        NomeTom = Util.San(parts[2]),
        NomeIbge = Util.San(parts[3]),
        Uf = Util.San(parts[4]).ToUpperInvariant()
    });
}

Console.WriteLine($"Registros lidos: {municipios.Count}");

var porUf = new Dictionary<string, List<Municipio>>(StringComparer.OrdinalIgnoreCase);

foreach (var m in municipios)
{
    if (!porUf.ContainsKey(m.Uf))
        porUf[m.Uf] = new List<Municipio>();

    porUf[m.Uf].Add(m);
}

var ufsOrdenadas = porUf.Keys
    .Where(uf => !string.Equals(uf, "EX", StringComparison.OrdinalIgnoreCase))
    .OrderBy(uf => uf, StringComparer.OrdinalIgnoreCase)
    .ToList();

Console.WriteLine("Calculando hash por município e gerando arquivos por UF ...");

foreach (var uf in ufsOrdenadas)
{
    var listaUf = porUf[uf];

    listaUf.Sort((a, b) =>
        string.Compare(a.NomePreferido, b.NomePreferido, StringComparison.OrdinalIgnoreCase));

    Console.WriteLine($"Processando UF: {uf} ({listaUf.Count} municípios)");

    var swUf = Stopwatch.StartNew();

    string outPath = Path.Combine(outRoot, $"municipios_hash_{uf}.csv");
    string jsonPath = Path.Combine(outRoot, $"municipios_hash_{uf}.json");
    string binPath = Path.Combine(outRoot, $"municipios_hash_{uf}.dat");

    var linhasCsv = new ConcurrentBag<string>();
    var listaJson = new ConcurrentBag<object>();

    int count = 0;

    await Parallel.ForEachAsync(listaUf, async (m, ct) =>
    {
        string password = m.ToConcatenatedString();

        byte[] salt = Util.BuildSalt(m.Ibge);

        string hashHex = Util.DeriveHashHex(
            password,
            salt,
            PBKDF2_ITERATIONS,
            HASH_BYTES
        );

        linhasCsv.Add(
            $"{m.Tom};{m.Ibge};{m.NomeTom};{m.NomeIbge};{m.Uf};{hashHex}"
        );

        listaJson.Add(new
        {
            m.Tom,
            m.Ibge,
            m.NomeTom,
            m.NomeIbge,
            m.Uf,
            Hash = hashHex
        });

        int atual = Interlocked.Increment(ref count);

        if (atual % 50 == 0 || atual == listaUf.Count)
        {
            Console.WriteLine(
                $"  Parcial: {atual}/{listaUf.Count} municípios processados para UF {uf} | Tempo parcial: {FormatTempo(swUf.ElapsedMilliseconds)}"
            );
        }

        await ValueTask.CompletedTask;
    });

    await using var fs = new FileStream(
        outPath,
        FileMode.Create,
        FileAccess.Write,
        FileShare.None,
        4096,
        true
    );

    await using var swOut = new StreamWriter(
        fs,
        new UTF8Encoding(false)
    );

    await swOut.WriteLineAsync("TOM;IBGE;NomeTOM;NomeIBGE;UF;Hash");

    foreach (var linha in linhasCsv.OrderBy(x => x))
    {
        await swOut.WriteLineAsync(linha);
    }

    var json = JsonSerializer.Serialize(
        listaJson.OrderBy(x => x.ToString()),
        new JsonSerializerOptions
        {
            WriteIndented = true
        }
    );

    await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8);

    await using var fsBin = new FileStream(
        binPath,
        FileMode.Create
    );

    await JsonSerializer.SerializeAsync(
        fsBin,
        listaJson
    );

    swUf.Stop();

    Console.WriteLine(
        $"UF {uf} concluída. Arquivos CSV, JSON e BIN gerados. Tempo total UF: {FormatTempo(swUf.ElapsedMilliseconds)}"
    );
}

Console.WriteLine();
Console.WriteLine("===== PESQUISA =====");

while (true)
{
    Console.WriteLine();
    Console.WriteLine("1 - Pesquisar por UF");
    Console.WriteLine("2 - Pesquisar por nome");
    Console.WriteLine("3 - Pesquisar por código IBGE");
    Console.WriteLine("0 - Sair");

    Console.Write("Escolha: ");

    var opcao = Console.ReadLine();

    if (opcao == "0")
        break;

    IEnumerable<Municipio> resultado = Enumerable.Empty<Municipio>();

    switch (opcao)
    {
        case "1":
            Console.Write("UF: ");
            var uf = (Console.ReadLine() ?? "").Trim().ToUpperInvariant();

            resultado = municipios.Where(x =>
                x.Uf.Equals(uf, StringComparison.OrdinalIgnoreCase));

            break;

        case "2":
            Console.Write("Nome: ");
            var nome = (Console.ReadLine() ?? "").Trim();

            resultado = municipios.Where(x =>
                x.NomePreferido.Contains(
                    nome,
                    StringComparison.OrdinalIgnoreCase));

            break;

        case "3":
            Console.Write("Código IBGE: ");
            var codigo = (Console.ReadLine() ?? "").Trim();

            resultado = municipios.Where(x =>
                x.Ibge.Contains(codigo));

            break;

        default:
            Console.WriteLine("Opção inválida.");
            continue;
    }

    var listaResultado = resultado.ToList();

    Console.WriteLine();
    Console.WriteLine($"Resultados encontrados: {listaResultado.Count}");
    Console.WriteLine();

    foreach (var m in listaResultado.Take(50))
    {
        Console.WriteLine(
            $"{m.Ibge} | {m.NomePreferido} | {m.Uf}"
        );
    }

    if (listaResultado.Count > 50)
    {
        Console.WriteLine();
        Console.WriteLine("Mostrando apenas os primeiros 50 resultados.");
    }
}

sw.Stop();

Console.WriteLine();
Console.WriteLine("===== RESUMO =====");
Console.WriteLine($"UFs geradas: {ufsOrdenadas.Count}");
Console.WriteLine($"Pasta de saída: {outRoot}");
Console.WriteLine($"Tempo total: {FormatTempo(sw.ElapsedMilliseconds)} ({sw.Elapsed})");
