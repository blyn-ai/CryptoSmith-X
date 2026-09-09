namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// The one sentence on this page that two places have to agree on.
///
/// Band 2's subtitle says what the books hold, and the picker button that selects the books carries
/// the same sentence so the script can put it back when the reader returns to that cut. Written
/// twice they would drift the first time either was edited, and the drift would be invisible: both
/// are true-looking sentences about the same frame.
/// </summary>
public static class V2Notes
{
    public static string Book(PairPageModel model)
    {
        if (model.Books.Count == 0)
        {
            return "no venue here has a level book in the last ten minutes";
        }

        // Считаем, а не заявляем: столько уровней и столько чисел лежит в последнем кадре по
        // этому активу прямо сейчас.
        // ЧТО ХРАНИМ, названное так же, как это называют карточки. Шапка писала «25 levels a
        // side», карточка под ней — «14 of 25 drawn»: два счёта одного и того же в одной полосе,
        // и читатель вправе решить, что один из них неверен.
        var levels = model.Books.Values.Select(b => (int)b.Levels).Max();
        var figures = model.Books.Values.Sum(b => (b.BidPx.Length + b.AskPx.Length) * 2);

        return $"{figures} figures held · up to {levels} levels a side · price and size at every "
            + "level, sides paired from the middle: the gap in the first row is the spread";
    }
}
