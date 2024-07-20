using System;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json.Linq;
using RestSharp; // Это для работы с RestSharp.RestClient

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Выберите действие:");
        Console.WriteLine("1. Генерация адреса");
        Console.WriteLine("2. Формирование и отправка транзакции");

        string choice = Console.ReadLine();

        switch (choice)
        {
            case "1":
                GenerateAddress();
                break;
            case "2":
                await CreateAndSendTransaction();
                break;
            default:
                Console.WriteLine("Неверный выбор. Пожалуйста, выберите 1 или 2.");
                break;
        }
    }

    static void GenerateAddress()
    {
        var mnemo = new Mnemonic(Wordlist.English, WordCount.Twelve);
        var hdroot = mnemo.DeriveExtKey();
        var pKey = hdroot.Derive(new KeyPath("m/84'/0'/0'/0/0"));
        var key1 = pKey.GetWif(Network.TestNet).PrivateKey;
        BitcoinSecret senderSecret = key1.GetWif(Network.TestNet);

        var addressSender = senderSecret.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.TestNet);

        Console.WriteLine("Mnemonic: " + mnemo);
        Console.WriteLine("Private Key: " + senderSecret);
        Console.WriteLine("Address: " + addressSender);
    }

    static async Task CreateAndSendTransaction()
    {
        // Ввод данных пользователя
        Console.WriteLine("Введите мнемоническую фразу:");
        string mnemonicPhrase = Console.ReadLine();

        var mnemo = new Mnemonic(mnemonicPhrase, Wordlist.English);
        var hdroot = mnemo.DeriveExtKey();
        var pKey = hdroot.Derive(new KeyPath("m/84'/0'/0'/0/0"));
        var key1 = pKey.GetWif(Network.TestNet).PrivateKey;
        BitcoinSecret senderSecret = key1.GetWif(Network.TestNet);
        var addressSender = senderSecret.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.TestNet).ToString();

        Console.WriteLine("Введите Bitcoin адрес получателя:");
        string receiverAddressString = Console.ReadLine();
        BitcoinAddress receiverAddress = BitcoinAddress.Create(receiverAddressString, Network.TestNet);

        Console.WriteLine("Введите сумму перевода (в BTC):");
        string amountInput = Console.ReadLine();
        decimal amountToSend;
        if (!decimal.TryParse(amountInput, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out amountToSend))
        {
            Console.WriteLine("Неверный формат суммы. Пожалуйста, введите правильное число.");
            return;
        }

        // Получаем UTXO для адреса отправителя
        var utxos = await GetAddressUTXOs(addressSender);

        if (utxos == null || !utxos.Any())
        {
            Console.WriteLine("Нет доступных UTXO.");
            return;
        }

        // Создаем транзакцию
        TransactionBuilder builder = Network.TestNet.CreateTransactionBuilder();

        foreach (var utxo in utxos)
        {
            var txId = new uint256(utxo["tx_hash"].ToString());
            var index = (uint)utxo["tx_output_n"];
            var amount = Money.Satoshis((long)utxo["value"]);

            // Получаем транзакцию из BlockCypher API
            var txHex = await GetTransactionHex(txId.ToString());
            if (string.IsNullOrEmpty(txHex))
            {
                Console.WriteLine($"Не удалось получить транзакцию {txId}");
                return;
            }

            // Парсим транзакцию
            var transaction = Transaction.Parse(txHex, Network.TestNet);

            // Создаем объект IndexedTxOut
            var indexedTxOut = new IndexedTxOut
            {
                N = index,
                TxOut = transaction.Outputs[index],
                Transaction = transaction
            };

            // Создаем объект Coin
            var coin = new Coin(indexedTxOut);

            // Добавляем Coin в транзакцию
            builder.AddCoins(coin);
        }

        builder.AddKeys(senderSecret.PrivateKey);
        builder.Send(receiverAddress, Money.Coins(amountToSend));
        builder.SendFees(Money.Satoshis(500)); // Установите комиссию за транзакцию
        builder.SetChange(BitcoinAddress.Create(addressSender, Network.TestNet));

        // Строим и подписываем транзакцию
        NBitcoin.Transaction tx = builder.BuildTransaction(true);

        // Проверяем, что транзакция правильно подписана
        if (!builder.Verify(tx))
        {
            Console.WriteLine("Транзакция не прошла проверку.");
            return;
        }

        // Отправляем транзакцию через BlockCypher
        var result = await BroadcastTransaction(tx.ToHex());
        if (result != null && result["tx"] != null)
        {
            Console.WriteLine($"Транзакция успешно отправлена. TXID: {result["tx"]["hash"]}");
        }
        else
        {
            Console.WriteLine("Ошибка при отправке транзакции.");
        }
    }

    public static async Task<string> GetTransactionHex(string txid)
    {
        var client = new RestClient($"https://api.blockcypher.com/v1/btc/test3/txs/{txid}?limit=50&includeHex=true");
        var request = new RestRequest { Method = Method.Get };

        var response = await client.ExecuteAsync(request);

        if (response.IsSuccessful)
        {
            var data = JObject.Parse(response.Content);
            return data["hex"].ToString();
        }

        return null;
    }

    public static async Task<JArray> GetAddressUTXOs(string address)
    {
        var client = new RestClient($"https://api.blockcypher.com/v1/btc/test3/addrs/{address}?unspentOnly=true");
        var request = new RestRequest { Method = Method.Get };

        var response = await client.ExecuteAsync(request);

        if (response.IsSuccessful)
        {
            var data = JObject.Parse(response.Content);
            return (JArray)data["txrefs"];
        }

        return null;
    }

    public static async Task<JObject> BroadcastTransaction(string txHex)
    {
        var client = new RestClient("https://api.blockcypher.com/v1/btc/test3/txs/push");
        var request = new RestRequest { Method = Method.Post };
        request.AddJsonBody(new { tx = txHex });

        var response = await client.ExecuteAsync(request);

        if (response.IsSuccessful)
        {
            return JObject.Parse(response.Content);
        }

        return null;
    }
}
