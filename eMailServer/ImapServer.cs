using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using NLog;
using TcpRequestHandler;

namespace eMailServer {
	public class ImapServer : ImapRequestHandler {
		protected new static Logger logger = LogManager.GetCurrentClassLogger();
		
		private User _user = new User();
		private ImapState _state = ImapState.Default;
		private string _lastClientId = String.Empty;
		
		public ImapServer() : base() {
			
		}
		
		public ImapServer(TcpClient client) : base(client) {
			this.Connected+= (object sender, TcpRequestEventArgs e) => {
				if (this.Verbose && e.RemoteEndPoint != null && e.LocalEndPoint != null) {
					logger.Debug("connected from remote [{0}:{1}] to local [{2}:{3}]",
						e.RemoteEndPoint.Address.ToString(),
					    e.RemoteEndPoint.Port,
					    e.LocalEndPoint.Address.ToString(),
					    e.LocalEndPoint.Port
					);
				}
				
				this.SendMessage("OK IMAP4rev1 Service Ready", "*");
			};
			
			this.Disconnected+= (object sender, TcpRequestEventArgs e) => {
				if (this.Verbose && e.RemoteEndPoint != null && e.LocalEndPoint != null) {
					logger.Debug("disconnected from remote [{0}:{1}] to local [{2}:{3}]",
						e.RemoteEndPoint.Address.ToString(),
					    e.RemoteEndPoint.Port,
					    e.LocalEndPoint.Address.ToString(),
					    e.LocalEndPoint.Port
					);
				}
			};
			
			this.LineReceived+= (object sender, TcpLineReceivedEventArgs e) => {
				logger.Info(String.Format("[{0}:{1}] Received Line: \"{2}\"", this._remoteEndPoint.Address.ToString(), this._remoteEndPoint.Port, e.Line));
				
				switch(this._state) {
					case ImapState.AuthenticatePlain:
						byte[] decodedBytesAuth = Convert.FromBase64String(e.Line);
						List<string> words = new List<string>();
						List<byte> byteWord = new List<byte>();
						foreach(byte currentByte in decodedBytesAuth) {
							if (currentByte == 0) {
								if (byteWord.Count > 0) {
									words.Add(Encoding.UTF8.GetString(byteWord.ToArray()));
								}
								
								byteWord = new List<byte>();
								continue;
							}
							
							byteWord.Add(currentByte);
						}
						if (byteWord.Count > 0) {
							words.Add(Encoding.UTF8.GetString(byteWord.ToArray()));
						}
						
						this._state = ImapState.Default;
						if (words.Count == 2) {
							if (words[0] != String.Empty && words[1] != String.Empty) {
								if (this._user.RefreshByUsernamePassword(words[0], words[1]) || this._user.RefreshByEMailPassword(words[0], words[1])) {
									this.SendMessage("OK PLAIN AUTHENTICATION successful", this._lastClientId);
								} else {
									this.SendMessage("NO invalid authentication", this._lastClientId);
								}
							} else {
								this.SendMessage("NO invalid authentication", this._lastClientId);
							}
						}
						break;
					
					case ImapState.Default:
						Match clientCommandMatch = Regex.Match(e.Line, @"^([^\s]+)\s+(\w+)(\s.+)?", RegexOptions.IgnoreCase);
						if (clientCommandMatch.Success) {
							this._lastClientId = clientCommandMatch.Groups[1].Value;
		
							switch(clientCommandMatch.Groups[2].Value.ToUpper()) {
								case "AUTHENTICATE":
									this.Authenticate(clientCommandMatch.Groups[3].Value.Trim().ToUpper());
									break;
		
								case "CAPABILITY":
									this.SendMessage("CAPABILITY IMAP4rev1 LOGINDISABLED AUTH=PLAIN", "*");
									this.SendMessage("OK CAPABILITY completed", this._lastClientId);
									break;
								
								case "CHECK":
									this.SendMessage("OK CHECK completed", this._lastClientId);
									break;
									
								case "FETCH":
									this.Fetch();
									break;
								
								case "LOGOUT":
									this.Logout((TcpRequestHandler.TcpRequestHandler)sender);
									break;
									
								case "NOOP":
									this.SendMessage("OK NOOP completed", this._lastClientId);
									break;
									
								case "SELECT":
									this.Select(clientCommandMatch.Groups[3].Value.ToUpper());
									break;
								
								case "STARTTLS":
									this.SendMessage("OK STARTTLS completed", this._lastClientId);
									break;
								
								case "UID":
									this.Uid(clientCommandMatch.Groups[3].Value.Trim());
									break;
								
								default:
									this.SendMessage("BAD " + clientCommandMatch.Groups[2].Value + " command not found", this._lastClientId);
									break;
							}
						} else {
							this.SendMessage("BAD invalid command line: " + e.Line, this._lastClientId);
						}
						break;
				}
			};
		}
		
		private void Authenticate(string authenticateMethod) {
			switch(authenticateMethod) {
				case "PLAIN":
					this.SendMessage("", "+");
					this._state = ImapState.AuthenticatePlain;
					break;
				
				default:
					this.SendMessage("BAD AUTHENTICATE invalid authenticate method", this._lastClientId);
					break;
			}
		}
		
		private void Fetch() {
			if (this._user.IsLoggedIn) {
				
			} else {
				this.SendMessage("BAD login required", this._lastClientId);
			}
		}
		
		private void Logout(TcpRequestHandler.TcpRequestHandler sender) {
			this.SendMessage("BYE IMAP4rev1 server terminating connection", "*");
			this.SendMessage("OK LOGOUT completed", this._lastClientId);
			sender.Close();
		}
		
		private void Select(string folder) {
			if (this._user.IsLoggedIn) {
				switch(folder) {
					case "INBOX":
					default:
						folder = "INBOX";
						break;
				}
				
				long eMailsCount = this._user.CountEMails(folder);
				this.SendMessage(eMailsCount + " EXISTS", "*");
				this.SendMessage(@"FLAGS (\Answered \Flagged \Deleted \Seen \Draft)", "*");
				this.SendMessage("OK [READ] SELECT completed", this._lastClientId);
			} else {
				this.SendMessage("BAD login required", this._lastClientId);
			}
		}
		
		private void Uid(string uidCommand) {
			if (this._user.IsLoggedIn) {
				Match uidCommandMatch = Regex.Match(uidCommand, @"^([^\s]+)\s+(([0-9]+)(:([0-9]+|\*))?)\s+(.+)?", RegexOptions.IgnoreCase);
				if (uidCommandMatch.Success) {
					switch(uidCommandMatch.Groups[1].Value.ToUpper()) {
						case "FETCH":
							try {
								string brackets = uidCommandMatch.Groups[6].Value.Trim(new char[] {' ', '\n', '\t', '\r', '(', ')'});
								FetchFields fetchFields = this.ParseFetchFields(brackets);
								
								int fromUid = Convert.ToInt32(uidCommandMatch.Groups[3].Value);
								int toUid = 10000;
								if (uidCommandMatch.Groups[5].Value == String.Empty) {
									toUid = fromUid;
								} else if (uidCommandMatch.Groups[5].Value != "*") {
									toUid = Convert.ToInt32(uidCommandMatch.Groups[5].Value);
								}
								List<eMail> emails = this._user.GetEmails(fromUid, toUid);
								int zeroMailCounter = 1;
								int uidMailCounter = fromUid;
								foreach(eMail mail in emails) {
									string fetch = String.Empty;
									bool othersThanFlags = false;
									foreach(string field in fetchFields.FieldList) {
										switch(field.ToUpper()) {
											case "UID":
												othersThanFlags = true;
												if (fetch != String.Empty) {
													fetch+= " ";
												}
												fetch+= "UID " + uidMailCounter;
												break;
											
											case "RFC822.SIZE":
												othersThanFlags = true;
												if (fetch != String.Empty) {
													fetch+= " ";
												}
												fetch+= "RFC822.SIZE " + mail.Message.Length;
												break;
											
											case "FLAGS":
												if (fetch != String.Empty) {
													fetch+= " ";
												}
												fetch+= "FLAGS (\\Seen)";
												break;
										}
									}
									
									if (fetchFields.Body) {
										othersThanFlags = true;
										fetch+= " BODY";
										string bodyString = String.Empty;
										if (fetchFields.BodyPeek) {
											if (fetchFields.Header) {
												fetch+= "[HEADER";
												if (fetchFields.HeaderFields) {
													fetch+= ".FIELDS (" + String.Join(" ", fetchFields.HeaderFieldList).ToUpper() + ")";
													
													bodyString+= this.BuildHeaderFields(fetchFields.HeaderFieldList, mail);
												}
												fetch+= "]";
											}
										} else {
											fetch+= "[]";
										}
										
										if (fetchFields.BodyMessage) {
											bodyString+= this.BuildHeaderFields(fetchFields.HeaderFieldList, mail);
											bodyString+= "\r\n" + mail.Message;
										}
										
										fetch+= " {" + (bodyString.Length) + "}\r\n" + bodyString + "\r\n";
									}
									
									if (!othersThanFlags) {
										if (fetch != String.Empty) {
											fetch+= " ";
										}
										fetch+= "UID " + uidMailCounter;
									}
									
									this.SendMessage(zeroMailCounter + @" FETCH (" + fetch + ")", "*");
									
									zeroMailCounter++;
									uidMailCounter++;
								}
								this.SendMessage("OK UID FETCH completed", this._lastClientId);
							} catch(FormatException) {
								this.SendMessage("BAD UID format exception " + uidCommand, this._lastClientId);
							} catch(OverflowException) {
								this.SendMessage("BAD UID overflow exception " + uidCommand, this._lastClientId);
							}
							break;
						
						case "SEARCH":
							break;
						
						default:
							this.SendMessage("BAD UID unknown parameter " + uidCommand, this._lastClientId);
							break;
					}
				}
			} else {
				this.SendMessage("BAD login required", this._lastClientId);
			}
		}
		
		private string BuildHeaderFields(List<string> headerFields, eMail currentEMail) {
			string bodyString = String.Empty;
			foreach(string headerField in headerFields) {
				switch(headerField.ToUpper()) {
					case "BCC":
						bodyString+= "Bcc: \r\n";
						break;
						
					case "CC":
						bodyString+= "Cc: \r\n";
						break;
						
					case "DATE":
						// Wed, 17 Jul 1996 02:23:25 -0700 (PDT)
						Console.WriteLine(currentEMail.HeaderDate.Equals(DateTime.MinValue));
						if (!currentEMail.HeaderDate.Equals(DateTime.MinValue)) {
							bodyString+= "Date: " + currentEMail.HeaderDate.ToString("ddd, dd MMM yyyy HH:mm:ss zzz (UTC)\r\n");
						} else {
							bodyString+= "Date: " + currentEMail.Time.ToString("ddd, dd MMM yyyy HH:mm:ss zzz (UTC)\r\n");
						}
						break;
						
					case "FROM":
						bodyString+= "From: \"" + currentEMail.HeaderFrom.Name + "\" <" + currentEMail.HeaderFrom.Address + ">\r\n";
						break;
						
					case "SUBJECT":
						bodyString+= "Subject: " + currentEMail.Subject + "\r\n";
						break;
					
					case "TO":
						if (currentEMail.HeaderTo.Count > 0) {
							bodyString+= "To: ";
							int headerToCounter = 0;
							foreach(eMailAddress headerTo in currentEMail.HeaderTo) {
								if (headerToCounter > 0) {
									bodyString+= ", ";
								}
								bodyString+= "\"" + headerTo.Name + "\" <" + headerTo.Address + ">\r\n";
								headerToCounter++;
							}
						}
						break;
				}
			}
			
			return bodyString;
		}
	}
}
