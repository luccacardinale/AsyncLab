# ⚡ AsyncLab

# Integrantes:
- Augusto Ferreira Rogel de Souza / RM 557709
- Heitor Prestes / RM 554823
- Lucca Cardinale / RM 556668

# O que foi feito:
- Refatoração do processamento de municípios para execução assíncrona
- Substituição de operações síncronas de arquivo por versões assíncronas (ReadAllLinesAsync, WriteAllTextAsync, WriteLineAsync)
- Substituição de WebClient por HttpClient com download assíncrono
- Implementação de paralelismo com Parallel.ForEachAsync para cálculo de hashes PBKDF2
- Uso de ConcurrentBag para armazenamento thread-safe durante processamento paralelo
- Melhoria de performance utilizando múltiplos núcleos da CPU no cálculo de hashes
- Ajuste de FileStream para escrita assíncrona com useAsync: true
- Manutenção da lógica original alterando apenas o necessário para suportar async/paralelismo
# Tempo de execução:
- Antes: 57 segundos
- Agora: 9 segundos


Após a implementação de operações assíncronas e processamento paralelo, houve uma redução significativa no tempo total de execução da aplicação. O maior ganho veio da paralelização do cálculo de hashes PBKDF2 utilizando Parallel.ForEachAsync, permitindo que múltiplos núcleos do processador fossem utilizados simultaneamente. Além disso, operações de leitura, escrita e download de arquivos passaram a ser executadas de forma assíncrona, reduzindo bloqueios durante o processamento.

### 🌐 Repositório Original
[https://github.com/profvinicius84/AsyncLab](https://github.com/profvinicius84/AsyncLab)


