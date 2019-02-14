using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.ServiceModel.Web;
using static System.Net.HttpStatusCode;

namespace Boggle
{
    public class BoggleService : IBoggleService
    {
        private static int gameID = 1;
        private readonly static Dictionary<String, UserInfo> users = new Dictionary<String, UserInfo>();
        private readonly static Dictionary<int, Game> games = new Dictionary<int, Game> { { gameID, new Game()} };
        private static readonly object sync = new object();


        /// <summary>
        /// The most recent call to SetStatus determines the response code used when
        /// an http response is sent.
        /// </summary>
        /// <param name="status"></param>
        private static void SetStatus(HttpStatusCode status)
        {
            WebOperationContext.Current.OutgoingResponse.StatusCode = status;
        }

        /// <summary>
        /// Returns a Stream version of index.html.
        /// </summary>
        /// <returns></returns>
        public Stream API()
        {
            SetStatus(OK);
            WebOperationContext.Current.OutgoingResponse.ContentType = "text/html";
            return File.OpenRead(AppDomain.CurrentDomain.BaseDirectory + "index.html");
        }

        public void CancelJoin(Token userToken)
        {
            //If UserToken is invalid or is not a player in the pending game, responds with status 403 (Forbidden).
            if (userToken.UserToken == null || !users.ContainsKey(userToken.UserToken) || (games[gameID].Player1Token != userToken.UserToken && games[gameID].Player2Token != userToken.UserToken))
            {
                SetStatus(Forbidden);
            }
            else // Otherwise, removes UserToken from the pending game and responds with status 200 (OK).
            {
                lock (sync)
                {
                    games[gameID].Player1Token = null;
                }

                SetStatus(OK);
            }

        }

        public string CreateUser(Username nickname)
        {
            lock (sync)
            {
                // Check for validity
                if (nickname.Nickname == null || nickname.Nickname.Trim().Length == 0)
                {
                    SetStatus(Forbidden);
                    return null;
                }
                // Add new user and return unique token
                else
                {
                    string UserToken = Guid.NewGuid().ToString();

                    UserInfo userInfo = new UserInfo();
                    userInfo.Nickname = nickname.Nickname;
                    userInfo.UserToken = UserToken;

                    
                    users.Add(UserToken, userInfo);

                    SetStatus(Created);
                    return UserToken;
                }
            }
        }

        public GameStatus GetGameStatus(int gameID, string brief)
        {
            // Checks if gameID is valid
            if (!games.ContainsKey(gameID))
            {
                SetStatus(Forbidden);
                return null;
            }
            // else return status and update game
            else
            {
                Game thisGame = games[gameID];
                GameStatus status = new GameStatus();

                // if game is pending
                if (thisGame.GameState == "pending")
                {
                    SetStatus(OK);
                    return new GameStatus() { GameState = "pending" };
                }
                // if game is active or completed and "Brief=yes" was a parameter
                if ((thisGame.GameState == "active" || thisGame.GameState == "complete") && brief == "yes")
                {
                    status = new GameStatus()
                    {
                        GameState = thisGame.GameState,
                        TimeLeft = thisGame.TimeLeft,
                        Player1 = new Player()
                        {
                            Score = thisGame.Player1Score
                        },
                        Player2 = new Player()
                        {
                            Score = thisGame.Player2Score
                        }
                    };
                }
                // if game is active and "Brief=yes" was not a parameter
                else if (thisGame.GameState == "active" && brief != "yes")
                {
                    status = new GameStatus()
                    {
                        GameState = thisGame.GameState,
                        Board = thisGame.GameBoard,
                        TimeLimit = thisGame.TimeLimit,
                        TimeLeft = thisGame.TimeLeft,
                        Player1 = new Player()
                        {
                            Nickname = users[thisGame.Player1Token].Nickname,
                            Score = thisGame.Player1Score
                        },
                        Player2 = new Player()
                        {
                            Nickname = users[thisGame.Player2Token].Nickname,
                            Score = thisGame.Player2Score
                        }
                    };
                }
                // if game is complete and user did not specify brief
                else if (thisGame.GameState == "completed" && brief != "yes")
                {
                    var Player1Scores = new HashSet<WordScore>();
                    foreach (KeyValuePair<string, int> kv in thisGame.Player1WordScores)
                    {
                        Player1Scores.Add(new WordScore() { Word = kv.Key, Score = kv.Value });
                    }

                    var Player2Scores = new HashSet<WordScore>();
                    foreach (KeyValuePair<string, int> kv in thisGame.Player2WordScores)
                    {
                        Player2Scores.Add(new WordScore() { Word = kv.Key, Score = kv.Value });
                    }

                    status = new GameStatus()
                    {
                        GameState = thisGame.GameState,
                        Board = thisGame.GameBoard,
                        TimeLimit = thisGame.TimeLimit,
                        TimeLeft = thisGame.TimeLeft,
                        Player1 = new Player()
                        {
                            Nickname = users[thisGame.Player1Token].Nickname,
                            Score = thisGame.Player1Score,
                            WordsPlayed = Player1Scores
                        },
                        Player2 = new Player()
                        {
                            Nickname = users[thisGame.Player2Token].Nickname,
                            Score = thisGame.Player2Score,
                            WordsPlayed = Player2Scores
                        }
                    };
                }

                // update game
                games[gameID].TimeLeft -= (DateTime.Now - thisGame.StartTime).Seconds;

                if (games[gameID].TimeLeft <= 0)
                {
                    games[gameID].GameState = "complete";
                    games[gameID].TimeLeft = 0;
                }

                SetStatus(OK);
                return status;
            }
        }

        public string JoinGame(JoinRequest joinRequest)
        {
            lock (sync)
            {
                string userToken = joinRequest.UserToken;
                int timeLimit = joinRequest.TimeLimit;

                //A user token is valid if it is non - null and identifies a user. Time must be between 5 and 120.
                if (userToken == null || !users.ContainsKey(userToken) || timeLimit < 5 || timeLimit > 120)
                {
                    SetStatus(Forbidden);
                    return null;
                }
                // Check if user is already in pending game.
                else if (games[gameID].Player1Token == userToken || games[gameID].Player2Token == userToken)
                {
                    SetStatus(Conflict);
                    return null;
                }

                // If player 1 is taken, user is player 2
                if (games[gameID].Player1Token != null && games[gameID].Player2Token == null)
                {
                    string GameID = gameID.ToString();


                    games[gameID].Player2Token = userToken;
                    StartPendingGame(timeLimit);

                    SetStatus(Created);
                    return GameID;
                }
                // if player 2 is taken, user is player 1
                else if (games[gameID].Player2Token != null && games[gameID].Player1Token == null)
                {
                    string GameID = gameID.ToString();

                    games[gameID].Player1Token = userToken;
                    StartPendingGame(timeLimit);

                    SetStatus(Created);
                    return GameID;
                }
                // if user is first to enter pending game
                else
                {
                    string GameID = gameID.ToString();

                    games[gameID].Player1Token = userToken;
                    games[gameID].TimeLimit = timeLimit;

                    SetStatus(Accepted);
                    return GameID;
                }
            }
        }

        private void StartPendingGame(int timeLimit)
        {
            // Starts a new active game
            int timeLeft = (games[gameID].TimeLimit + timeLimit) / 2;
            games[gameID].GameState = "active";
            games[gameID].GameBoard = new BoggleBoard().ToString();
            games[gameID].TimeLimit = timeLeft;
            games[gameID].TimeLeft = timeLeft;
            games[gameID].StartTime = DateTime.Now;
            gameID++;
            games.Add(gameID, new Game());
        }

        public string PlayWord(string gameIDString, WordPlayed wordPlayed)
        {
            lock (sync)
            {
                int gameID = int.Parse(gameIDString);
                string UserToken = wordPlayed.UserToken;
                string Word = wordPlayed.Word.ToUpper();

                // If Word is null or empty when trimmed, or if GameID or UserToken is missing or invalid,
                // or if UserToken is not a player in the game identified by GameID, responds with response code 403 (Forbidden).
                if (Word == null || Word.Trim() == string.Empty || !users.ContainsKey(UserToken) ||
                    (games[gameID].Player1Token != UserToken && games[gameID].Player2Token != UserToken))
                {
                    SetStatus(Forbidden);
                    return null;
                }
                // Otherwise, if the game state is anything other than "active", responds with response code 409(Conflict).
                else if (games[gameID].GameState != "active")
                {
                    SetStatus(Conflict);
                    return null;
                }
                else
                {
                    // Otherwise, records the trimmed Word as being played by UserToken in the game identified by GameID.
                    // Returns the score for Word in the context of the game(e.g. if Word has been played before the score is zero). 
                    // Responds with status 200(OK).Note: The word is not case sensitive.
                    BoggleBoard board = new BoggleBoard(games[gameID].GameBoard);
                    int score = 0;

                    // TODO Check if word exists in the dictionary
                    if (board.CanBeFormed(Word))
                    {

                        if (Word.Length > 2) score++;
                        if (Word.Length > 4) score++;
                        if (Word.Length > 5) score++;
                        if (Word.Length > 6) score += 2;
                        if (Word.Length > 7) score += 6;

                        if (games[gameID].Player1Token == UserToken)
                        {
                            if (games[gameID].Player2WordScores.ContainsKey(Word))
                            {
                                games[gameID].Player2Score -= games[gameID].Player2WordScores[Word];
                                games[gameID].Player2WordScores[Word] = score = 0;
                            }

                            if (games[gameID].Player1WordScores.ContainsKey(Word))
                            {
                                score = 0;
                            }
                            else
                            {
                                games[gameID].Player1WordScores.Add(Word, score);
                            }
                        }
                        else if (games[gameID].Player2Token == UserToken)
                        {
                            if (games[gameID].Player1WordScores.ContainsKey(Word))
                            {
                                games[gameID].Player1Score -= games[gameID].Player1WordScores[Word];
                                games[gameID].Player1WordScores[Word] = score = 0;
                            }

                            if (games[gameID].Player2WordScores.ContainsKey(Word))
                            {
                                score = 0;
                            }
                            else
                            {
                                games[gameID].Player2WordScores.Add(Word, score);
                            }
                        }
                    }

                    SetStatus(OK);
                    return score.ToString();
                }
            }
        }
    }
}
