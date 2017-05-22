using Facepunch;
using Oxide.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO; 
using UnityEngine;

namespace Oxide.Plugins
{
	[Info("Copy Paste", "Reneb", "3.1.6", ResourceId = 716)] 
	[Description("Copy and paste your buildings to save them or move them")]

	class CopyPaste : RustPlugin
	{
		private int copyLayer 		= LayerMask.GetMask("Construction", "Construction Trigger", "Trigger", "Deployed", "Tree", "AI");
		private int collisionLayer 	= LayerMask.GetMask("Construction", "Construction Trigger", "Trigger", "Deployed", "Default");
		private int groundLayer 	= LayerMask.GetMask("Terrain", "Default");
		private int rayCopy 		= LayerMask.GetMask("Construction", "Deployed", "Tree", "Resource", "Prevent Building");
		private int rayPaste 		= LayerMask.GetMask("Construction", "Deployed", "Tree", "Terrain", "World", "Water", "Prevent Building");
		
		private string copyPermission = "copypaste.copy";
		private string pastePermission = "copypaste.paste";
		private string undoPermission = "copypaste.undo";
		private string subDirectory = "copypaste/";

		private Dictionary<string, List<BaseEntity>> lastPastes = new Dictionary<string, List<BaseEntity>>();

		private DataFileSystem dataSystem = Interface.Oxide.DataFileSystem;

		private enum CopyMechanics { Building, Proximity }

		//Hooks

		private void Init()
		{
			permission.RegisterPermission(copyPermission, this);
			permission.RegisterPermission(pastePermission, this);
			permission.RegisterPermission(undoPermission, this);

			Dictionary<string, Dictionary<string, string>> compiledLangs = new Dictionary<string, Dictionary<string, string>>();

			foreach(var line in messages)
			{
				foreach(var translate in line.Value)
				{
					if(!compiledLangs.ContainsKey(translate.Key))
						compiledLangs[translate.Key] = new Dictionary<string, string>();
					
					compiledLangs[translate.Key][line.Key] = translate.Value;
				}				
			}

			foreach(var cLangs in compiledLangs)
			{
				lang.RegisterMessages(cLangs.Value, this, cLangs.Key);
			} 
		}

		//API
		
		object TryCopyFromSteamID(ulong userID, string filename, string[] args)
		{
			var player = BasePlayer.FindByID(userID);

			if(player == null) 
				return Lang("NOT_FOUND_PLAYER", player.UserIDString);
			
			var ViewAngles = Quaternion.Euler(player.GetNetworkRotation());
			BaseEntity sourceEntity;
			Vector3 sourcePoint;

			if(!FindRayEntity(player.eyes.position, ViewAngles * Vector3.forward, out sourcePoint, out sourceEntity, rayCopy))
			{
				return Lang("NO_ENTITY_RAY", player.UserIDString);
			}

			return TryCopy(sourcePoint, sourceEntity.transform.rotation.ToEulerAngles(), filename, ViewAngles.ToEulerAngles().y, args);
		}
		
		object TryPasteFromVector3(Vector3 startPos, Vector3 direction, string filename, string[] args)
		{
			return TryPaste(startPos, filename, null, direction.y, args);
		}

		object TryPasteFromSteamID(ulong userID, string filename, string[] args)
		{
			var player = BasePlayer.FindByID(userID);

			if(player == null) 
				return Lang("NOT_FOUND_PLAYER", player.UserIDString);

			var ViewAngles = Quaternion.Euler(player.GetNetworkRotation());
			BaseEntity sourceEntity;
			Vector3 sourcePoint;

			if(!FindRayEntity(player.eyes.position, ViewAngles * Vector3.forward, out sourcePoint, out sourceEntity, rayPaste)) 
			{
				return Lang("NO_ENTITY_RAY", player.UserIDString);
			}

			return TryPaste(sourcePoint, filename, player, ViewAngles.ToEulerAngles().y, args);
		}

		//Other methods

		private object CheckCollision(List<Dictionary<string,object>> entities, Vector3 startPos, float radius)
		{
			foreach(var entityobj in entities)
			{
				var pos = (Vector3)entityobj["position"];
				var rot = (Quaternion)entityobj["rotation"];

				foreach(var collider in Physics.OverlapSphere(pos, radius, collisionLayer))
				{
					return string.Format("Something is blocking the paste ({0})", collider.gameObject.name);
				}
			}
			
			return true;
		}

		private object Copy(Vector3 sourcePos, Vector3 sourceRot, string filename, float RotationCorrection, CopyMechanics copyMechanics, float range, bool saveBuildings, bool saveDeployables, bool saveInventories)
		{
			var rawData = new List<object>();
			var copy = CopyProcess(sourcePos, sourceRot, RotationCorrection, range, saveBuildings, saveDeployables, saveInventories, copyMechanics);

			if(copy is string) 
				return copy;

			rawData = copy as List<object>;

			var defaultData = new Dictionary<string, object>
			{
				{"position", new Dictionary<string, object>
					{
						{"x", sourcePos.x.ToString()  },
						{"y", sourcePos.y.ToString() },
						{"z", sourcePos.z.ToString() }
					}
				},
				{"rotationy", sourceRot.y.ToString() },
				{"rotationdiff", RotationCorrection.ToString() }
			};

			string path = subDirectory + filename;
			var CopyData = dataSystem.GetDatafile(path);

			CopyData.Clear();
			CopyData["default"] = defaultData;
			CopyData["entities"] = rawData;

			dataSystem.SaveDatafile(path);

			return true;
		}

		private object CopyProcess(Vector3 sourcePos, Vector3 sourceRot, float RotationCorrection, float range, bool saveBuildings, bool saveDeployables, bool saveInventories, CopyMechanics copyMechanics)
		{
			var rawData = new List<object>();
			var houseList = new List<BaseEntity>();
			var checkFrom = new List<Vector3> { sourcePos };
			uint buildingid = 0;
			int current = 0;

			try
			{
				while(true)
				{
					if(current >= checkFrom.Count) 
						break;

					List<BaseEntity> list = Pool.GetList<BaseEntity>();
					Vis.Entities<BaseEntity>(checkFrom[current], range, list, copyLayer);

					for(int i = 0; i < list.Count; i++)
					{
						var entity = list[i];
						
						if(isValid(entity) && !houseList.Contains(entity))
						{
							houseList.Add(entity);
							
							if(copyMechanics == CopyMechanics.Building)
							{
								BuildingBlock buildingblock = entity.GetComponentInParent<BuildingBlock>();

								if(buildingblock)
								{
									if(buildingid == 0) 
										buildingid = buildingblock.buildingID;
									else if(buildingid != buildingblock.buildingID) 
										continue;
								}
							}
							
							if(!checkFrom.Contains(entity.transform.position)) 
								checkFrom.Add(entity.transform.position);

							if(!saveBuildings && entity.GetComponentInParent<BuildingBlock>() != null) 
								continue;
							
							if(!saveDeployables && (entity.GetComponentInParent<BuildingBlock>() == null && entity.GetComponent<BaseCombatEntity>() != null)) 
								continue;
							
							rawData.Add(EntityData(entity, sourcePos, sourceRot, entity.transform.position, entity.transform.rotation.ToEulerAngles(), RotationCorrection, saveInventories));
						}
					}
					
					current++;
				}
			} catch (Exception e) {
				return e.Message;
			}

			return rawData;
		}

		private Dictionary<string, object> EntityData(BaseEntity entity, Vector3 sourcePos, Vector3 sourceRot, Vector3 entPos, Vector3 entRot, float diffRot, bool saveInventories)
		{
			var normalizedPos = NormalizePosition(sourcePos, entPos, diffRot);
			var normalizedRot = entRot.y - diffRot;

			var data = new Dictionary<string, object>
			{
				{ "prefabname", entity.PrefabName },
				{ "skinid", entity.skinID },
				{ "pos", new Dictionary<string,object>
					{
						{ "x", normalizedPos.x.ToString() },
						{ "y", normalizedPos.y.ToString() },
						{ "z", normalizedPos.z.ToString() }
					}
				},
				{ "rot", new Dictionary<string,object>
					{
						{ "x", entRot.x.ToString() },
						{ "y", normalizedRot.ToString() },
						{ "z", entRot.z.ToString() },
					}
				}
			};

			if(entity.HasSlot(BaseEntity.Slot.Lock))
				TryCopyLock(entity, data);

			var buildingblock = entity.GetComponentInParent<BuildingBlock>();

			if(buildingblock != null )
			{
				data.Add("grade", buildingblock.grade);
			}

			var box = entity.GetComponentInParent<StorageContainer>();

			if(box != null)
			{
				var itemlist = new List<object>();
				
				if(saveInventories)
				{
					foreach(Item item in box.inventory.itemList)
					{
						var itemdata = new Dictionary<string, object>
						{
							{"condition", item.condition.ToString() },
							{"id", item.info.itemid },
							{"amount", item.amount },
							{"skinid", item.skin },
						};
						
						var heldEnt = item.GetHeldEntity();
						
						if(heldEnt != null)
						{
							var projectiles = heldEnt.GetComponent<BaseProjectile>();
							
							if(projectiles != null)
							{
								var magazine = projectiles.primaryMagazine;
								
								if(magazine != null)
								{
									itemdata.Add("magazine", new Dictionary<string, object> 
									{ 
										{ magazine.ammoType.itemid.ToString(), magazine.contents } 
									});
								}
							}
						}

						if(item?.contents?.itemList != null)
						{
							var contents = new List<object>();
							
							foreach(Item itemContains in item.contents.itemList)
							{
								contents.Add(new Dictionary<string, object>
								{
									{"id", itemContains.info.itemid },
									{"amount", itemContains.amount },
								});
							}
							
							itemdata["items"] = contents;
						}

						itemlist.Add(itemdata);
					}
				}
				
				data.Add("items", itemlist);
			}

			var sign = entity.GetComponentInParent<Signage>();

			if(sign != null)
			{
				var imageByte = FileStorage.server.Get(sign.textureID, FileStorage.Type.png, sign.net.ID);
				
				data.Add("sign", new Dictionary<string, object>
				{
					{"locked", sign.IsLocked() }
				});
				
				if(sign.textureID > 0 && imageByte != null) 
					((Dictionary<string, object>)data["sign"]).Add("texture", Convert.ToBase64String(imageByte));
			}

			return data;
		}

		private object FindBestHeight(List<Dictionary<string,object>> entities, Vector3 startPos)
		{
			float minHeight = 0f, maxHeight = 0f;

			foreach(var entity in entities)
			{
				if(((string)entity["prefabname"]).Contains("/foundation/"))
				{
					var foundHeight = GetGround((Vector3)entity["position"]);
					
					if(foundHeight != null)
					{
						var height = (Vector3)foundHeight;
						
						if(height.y > maxHeight) 
							maxHeight = height.y;
						
						if(height.y < minHeight) 
							minHeight = height.y;
					}
				}
			}

			if(maxHeight - minHeight > 3f) 
				return "The ground is too steep";

			maxHeight += 1f;

			return maxHeight;
		}

		private bool FindRayEntity(Vector3 sourcePos, Vector3 sourceDir, out Vector3 point, out BaseEntity entity, int rayLayer)
		{
			RaycastHit hitinfo;
			entity = null;
			point = Vector3.zero;

			if(!Physics.Raycast(sourcePos, sourceDir, out hitinfo, 1000f, rayLayer)) 
				return false;
			
			entity = hitinfo.GetEntity();		
			point = hitinfo.point;
			
			return true;
		}

		private object GetGround(Vector3 pos)
		{
			RaycastHit hitInfo;

			if(Physics.Raycast(pos, Vector3.up, out hitInfo, groundLayer))
			{
				return hitInfo.point;
			}

			if(Physics.Raycast(pos, Vector3.down, out hitInfo, groundLayer))
			{
				return hitInfo.point;
			}

			return null;
		}

		private bool HasAccess(BasePlayer player, string permName) 
		{
			return player.net.connection.authLevel > 1 || permission.UserHasPermission(player.UserIDString, permName); 
		}

		private bool isValid(BaseEntity entity) 
		{ 
			return (entity.GetComponentInParent<BuildingBlock>() != null || entity.GetComponentInParent<BaseCombatEntity>() != null || entity.GetComponentInParent<Spawnable>() != null); 
		}

		private string Lang(string key, string userID = null, params object[] args) => string.Format(lang.GetMessage(key, this, userID), args);	

		private Vector3 NormalizePosition(Vector3 InitialPos, Vector3 CurrentPos, float diffRot)
		{
			var transformedPos = CurrentPos - InitialPos;
			var newX = (transformedPos.x * (float)System.Math.Cos(-diffRot)) + (transformedPos.z * (float)System.Math.Sin(-diffRot));
			var newZ = (transformedPos.z * (float)System.Math.Cos(-diffRot)) - (transformedPos.x * (float)System.Math.Sin(-diffRot));

			transformedPos.x = newX;
			transformedPos.z = newZ;

			return transformedPos;
		}

		private List<BaseEntity> Paste(List<Dictionary<string,object>> entities, Vector3 startPos, BasePlayer player, bool checkPlaced)
		{
			bool unassignid = true;
			uint buildingid = 0;
			var pastedEntities = new List<BaseEntity>();
			
			foreach(var data in entities)
			{
				try
				{
					var prefabname = (string)data["prefabname"];
					var skinid = ulong.Parse(data["skinid"].ToString());
					var pos = (Vector3)data["position"];
					var rot = (Quaternion)data["rotation"];

					bool isPlaced = false;
					
					if(checkPlaced)
					{
						foreach(var col in Physics.OverlapSphere(pos, 1f))
						{
							var ent = col.GetComponentInParent<BaseEntity>();
							
							if(ent != null)
							{
								if(ent.PrefabName == prefabname && ent.transform.position == pos && ent.transform.rotation == rot)
								{
									isPlaced = true;
									break;
								}
							}
						}
					}

					if(isPlaced) 
						continue;

					var entity = GameManager.server.CreateEntity(prefabname, pos, rot, true);
					
					if(entity != null)
					{
						entity.transform.position = pos;
						entity.transform.rotation = rot;
						entity.SendMessage("SetDeployedBy", player, SendMessageOptions.DontRequireReceiver);

						var buildingblock = entity.GetComponentInParent<BuildingBlock>();
						
						if(buildingblock != null)
						{
							buildingblock.blockDefinition = PrefabAttribute.server.Find<Construction>(buildingblock.prefabID);
							buildingblock.SetGrade((BuildingGrade.Enum)data["grade"]);
							
							if(unassignid)
							{
								buildingid = BuildingBlock.NewBuildingID();
								unassignid = false;
							}
							
							buildingblock.buildingID = buildingid;
						}
						
						entity.skinID = skinid;
						entity.Spawn();

						var basecombat = entity.GetComponentInParent<BaseCombatEntity>();
						
						if(basecombat != null)
						{
							basecombat.ChangeHealth(basecombat.MaxHealth());
						}

						if(entity.HasSlot(BaseEntity.Slot.Lock))
						{
							TryPasteLock(entity, data);
						}

						var box = entity.GetComponentInParent<StorageContainer>();
						
						if(box != null)
						{
							box.inventory.Clear();
							var items = data["items"] as List<object>;
							var itemlist = new List<ItemAmount>();
							
							foreach(var itemDef in items)
							{
								var item = itemDef as Dictionary<string, object>;
								var itemid = Convert.ToInt32(item["id"]);
								var itemamount = Convert.ToInt32(item["amount"]);
								var itemskin = ulong.Parse(item["skinid"].ToString());
								var itemcondition = Convert.ToSingle(item["condition"]);

								var i = ItemManager.CreateByItemID(itemid, itemamount, itemskin);
								
								if(i != null)
								{
									i.condition = itemcondition;

									if(item.ContainsKey("magazine"))
									{
										var heldent = i.GetHeldEntity();
										
										if(heldent != null)
										{
											var projectiles = heldent.GetComponent<BaseProjectile>();
											
											if(projectiles != null)
											{
												var magazine = item["magazine"] as Dictionary<string, object>;
												var ammotype = int.Parse(magazine.Keys.ToArray()[0]);
												var ammoamount = int.Parse(magazine[ammotype.ToString()].ToString());	
												
												projectiles.primaryMagazine.ammoType = ItemManager.FindItemDefinition(ammotype);
												projectiles.primaryMagazine.contents = ammoamount;
											}
											
											//TODO: Compability water 
											
											if(item.ContainsKey("items"))
											{	
												var itemContainsList = item["items"] as List<object>;
												
												foreach(var itemContains in itemContainsList)
												{
													var contents = itemContains as Dictionary<string, object>;
													
													i.contents.AddItem(ItemManager.FindItemDefinition(Convert.ToInt32(contents["id"])), Convert.ToInt32(contents["amount"]));		
												}
											}											
										}
									}
									
									i?.MoveToContainer(box.inventory).ToString();
								}
							};
						}

						var sign = entity.GetComponentInParent<Signage>();
						
						if(sign != null)
						{
							var signData = data["sign"] as Dictionary<string, object>;
							
							if(signData.ContainsKey("texture"))
							{
								var stream = new MemoryStream();
								var stringSign = Convert.FromBase64String(signData["texture"].ToString());
								stream.Write(stringSign, 0, stringSign.Length);
								sign.textureID = FileStorage.server.Store(stream, FileStorage.Type.png, sign.net.ID);
								stream.Position = 0;
								stream.SetLength(0);
							}
							
							if(Convert.ToBoolean(signData["locked"]))
								sign.SetFlag(BaseEntity.Flags.Locked, true);
							
							sign.SendNetworkUpdate();
						}

						pastedEntities.Add(entity);
					}
				} catch(Exception e) {
					PrintError(string.Format("Trying to paste {0} send this error: {1}", data["prefabname"].ToString(), e.Message));
				}
			}
			return pastedEntities;
		}

		private List<Dictionary<string, object>> PreLoadData(List<object> entities, Vector3 startPos, float RotationCorrection, bool deployables, bool inventories)
		{
			var eulerRotation = new Vector3(0f, RotationCorrection, 0f);
			var quaternionRotation = Quaternion.EulerRotation(eulerRotation);
			var preloaddata = new List<Dictionary<string, object>>();
			
			foreach(var entity in entities)
			{
				var data = entity as Dictionary<string, object>;
				
				if(!deployables && !data.ContainsKey("grade")) 
					continue;
				
				var pos = (Dictionary<string, object>)data["pos"];
				var rot = (Dictionary<string, object>)data["rot"];
				var fixedRotation = Quaternion.EulerRotation(eulerRotation + new Vector3(Convert.ToSingle(rot["x"]), Convert.ToSingle(rot["y"]), Convert.ToSingle(rot["z"])));
				var tempPos = quaternionRotation * (new Vector3(Convert.ToSingle(pos["x"]), Convert.ToSingle(pos["y"]), Convert.ToSingle(pos["z"])));
				Vector3 newPos = tempPos + startPos;
				
				data.Add("position", newPos);
				data.Add("rotation", fixedRotation);
				
				if(!inventories && data.ContainsKey("items")) 
					data["items"] = new List<object>();
				
				preloaddata.Add(data);
			}
			
			return preloaddata;
		}

		private object TryCopy(Vector3 sourcePos, Vector3 sourceRot, string filename, float RotationCorrection, string[] args)
		{
			bool saveInventories = true, saveDeployables = true, saveBuilding = true;
			CopyMechanics copyMechanics = CopyMechanics.Building;
			float radius = 3f;
			
			for(int i = 0; ; i = i + 2)
			{
				if(i >= args.Length)  
					break;
				
				if(i+1 >= args.Length)
				{
					return Lang("SYNTAX_COPY", null);
				}
				
				switch(args[i].ToLower())
				{
					case "r":
					case "rad":
					case "radius":
						if(!float.TryParse(args[i+1], out radius))
						{
							return "radius must be a number";
						}
						break;
					case "mechanics":
					case "m":
					case "mecha":
						switch(args[i+1].ToLower())
						{
							case "building":
							case "build":
							case "b":
								copyMechanics = CopyMechanics.Building;
								break;
							case "proximity":
							case "prox":
							case "p":
								copyMechanics = CopyMechanics.Proximity;
								break;
						}
						break;
					case "i":
					case "inventories":
					case "inv":
						if(!bool.TryParse(args[i + 1], out saveInventories))
						{
							return "save inventories needs to be true or false";
						}
						break;
					case "b":
					case "building":
					case "structure":
						if(!bool.TryParse(args[i + 1], out saveBuilding))
						{
							return "save buildings needs to be true or false";
						}
						break;
					case "d":
					case "deployables":
						if(!bool.TryParse(args[i + 1], out saveDeployables))
						{
							return "save deployables needs to be true or false";
						}
						break;

					default:
						return Lang("SYNTAX_COPY", null);
						break;
				}
			}

			return Copy(sourcePos, sourceRot, filename, RotationCorrection, copyMechanics, radius, saveBuilding, saveDeployables, saveInventories);
		}
				
		private void TryCopyLock(BaseEntity lockableEntity, IDictionary<string, object> housedata)
		{
			var slotentity = lockableEntity.GetSlot(BaseEntity.Slot.Lock);

			if(slotentity != null)
			{
				var codedata = new Dictionary<string, object>
				{
					{"prefabname", slotentity.PrefabName}
				};

				if(slotentity.GetComponent<CodeLock>())
				{
					codedata.Add("code", slotentity.GetComponent<CodeLock>().code.ToString());
				} else if(slotentity.GetComponent<KeyLock>()) {
					var @lock = slotentity.GetComponent<KeyLock>();
					var code = @lock.keyCode;
					if(@lock.firstKeyCreated) code |= 0x80;
					codedata.Add("code", code.ToString());
				}
				
				housedata.Add("lock",codedata);
			}
		}

		private object TryPaste(Vector3 startPos, string filename, BasePlayer player, float RotationCorrection, string[] args)
		{
			var steamid = player == null ? null : player.UserIDString;

			string path = subDirectory + filename;

			if(!dataSystem.ExistsDatafile(path)) 
			{
				return Lang("FILE_NOT_EXISTS", steamid);
			}

			var data = dataSystem.GetDatafile(path);

			if(data["default"] == null || data["entities"] == null)
			{
				return Lang("FILE_EMPTY", steamid);
			}

			float heightAdj = 0f, blockCollision = 0f;
			bool checkPlaced = false, autoHeight = false, inventories = true, deployables = true;

			for(int i = 0; ; i = i + 2)
			{
				if(i >= args.Length) 
					break;
				
				if(i + 1 >= args.Length)
				{
					return Lang("SYNTAX_PASTE_OR_PASTEBACK", steamid);
				}
				
				switch(args[i].ToLower())
				{
					case "autoheight":
						if(!bool.TryParse(args[i + 1], out autoHeight))
						{
							return "autoheight must be true or false";
						}
						
						break;
					case "height":
						if(!float.TryParse(args[i + 1], out heightAdj))
						{
							return "height must be a number";
						}
						
						break;
					case "checkplaced":
						if(!bool.TryParse(args[i + 1], out checkPlaced))
						{
							return "checkplaced must be true or false";
						}

						break;
					case "blockcollision":
						if(!float.TryParse(args[i + 1], out blockCollision))
						{
							return "blockcollision must be a number, 0 will deactivate the option";
						}
						
						break;
					case "deployables":
						if(!bool.TryParse(args[i + 1], out deployables))
						{
							return "deployables must be true or false";
						}

						break;
					case "inventories":
						if(!bool.TryParse(args[i + 1], out inventories))
						{
							return "inventories must be true or false";
						}
						
						break;
					default:
						return Lang("SYNTAX_PASTE_OR_PASTEBACK", steamid);
						break;
				}
			}

			startPos.y += heightAdj;

			var preloadData = PreLoadData(data["entities"] as List<object>, startPos, RotationCorrection, deployables, inventories);

			if(autoHeight)
			{
				var bestHeight = FindBestHeight(preloadData, startPos);
				
				if(bestHeight is string)
				{
					return bestHeight;
				}
				
				heightAdj = (float)bestHeight - startPos.y;

				foreach(var entity in preloadData)
				{
					var pos = ((Vector3)entity["position"]);
					pos.y += heightAdj;
					entity["position"] = pos;
				}				
			}

			if(blockCollision > 0f)
			{
				var collision = CheckCollision(preloadData, startPos, blockCollision);
				
				if(collision is string)
				{
					return collision;
				}
			}

			return Paste(preloadData, startPos, player, checkPlaced);
		}

		private void TryPasteLock(BaseEntity lockableEntity, Dictionary<string, object> structure)
		{
			BaseEntity lockentity = null;

			if(structure.ContainsKey("lock"))
			{
				var lockdata = structure["lock"] as Dictionary<string, object>;
				lockentity = GameManager.server.CreateEntity((string)lockdata["prefabname"], Vector3.zero, new Quaternion(), true);
				
				if(lockentity != null)
				{
					lockentity.gameObject.Identity();
					lockentity.SetParent(lockableEntity, "lock");
					lockentity.OnDeployed(lockableEntity);
					lockentity.Spawn();
					
					lockableEntity.SetSlot(BaseEntity.Slot.Lock, lockentity);
					
					if(lockentity.GetComponent<CodeLock>())
					{
						var code = (string)lockdata["code"];
						
						if(!string.IsNullOrEmpty(code))
						{
							var @lock = lockentity.GetComponent<CodeLock>();
							@lock.code = code;
							@lock.SetFlag(BaseEntity.Flags.Locked, true);
						}
					} else if(lockentity.GetComponent<KeyLock>()) {
						var code = Convert.ToInt32(lockdata["code"]);
						var @lock = lockentity.GetComponent<KeyLock>();
						
						if((code & 0x80) != 0)
						{
							@lock.keyCode = (code & 0x7F);
							@lock.firstKeyCreated = true;
							@lock.SetFlag(BaseEntity.Flags.Locked, true);
						}
					}
				}
			}
		}

		private object TryPlaceback(string filename, BasePlayer player, string[] args)
		{
			string path = subDirectory + filename;

			if(!dataSystem.ExistsDatafile(path)) 
			{
				return Lang("FILE_NOT_EXISTS", player.UserIDString);
			}

			var data = dataSystem.GetDatafile(path);

			if(data["default"] == null || data["entities"] == null)
			{
				return Lang("FILE_EMPTY", player.UserIDString);
			}

			var defaultdata = data["default"] as Dictionary<string, object>;
			var pos = defaultdata["position"] as Dictionary<string, object>;
			var startPos = new Vector3(Convert.ToSingle(pos["x"]), Convert.ToSingle(pos["y"]), Convert.ToSingle(pos["z"]));
			var RotationCorrection = Convert.ToSingle(defaultdata["rotationdiff"]);

			return TryPaste(startPos, filename, player, RotationCorrection, args);
		}

		//Сhat commands

		[ChatCommand("copy")]
		private void cmdChatCopy(BasePlayer player, string command, string[] args)
		{
			if(!HasAccess(player, copyPermission)) 
			{ 
				SendReply(player, Lang("NO_ACCESS", player.UserIDString)); 
				return; 
			}

			if(args.Length < 1) 
			{ 
				SendReply(player, Lang("SYNTAX_COPY", player.UserIDString)); 
				return; 
			}

			var savename = args[0];
			var success = TryCopyFromSteamID(player.userID, savename, args.Skip(1).ToArray());

			if(success is string) 
			{
				SendReply(player, (string)success);
				return;
			}

			SendReply(player, Lang("COPY_SUCCESS", player.UserIDString, savename));
		}

		[ChatCommand("paste")]
		private void cmdChatPaste(BasePlayer player, string command, string[] args)
		{
			if(!HasAccess(player, pastePermission))
			{ 
				SendReply(player, Lang("NO_ACCESS", player.UserIDString)); 
				return; 
			}

			if(args.Length < 1) 
			{ 
				SendReply(player, Lang("SYNTAX_PASTE_OR_PASTEBACK", player.UserIDString)); 
				return; 
			}

			var loadname = args[0];
			var success = TryPasteFromSteamID(player.userID, loadname, args.Skip(1).ToArray());

			if(success is string)
			{
				SendReply(player, (string)success);
				return;
			}

			if(lastPastes.ContainsKey(player.UserIDString)) 
				lastPastes.Remove(player.UserIDString);

			lastPastes.Add(player.UserIDString,(List<BaseEntity>)success);

			SendReply(player, Lang("PASTE_SUCCESS", player.UserIDString));
		}

		[ChatCommand("pasteback")]
		private void cmdChatPasteBack(BasePlayer player, string command, string[] args)
		{
			if(!HasAccess(player, pastePermission)) 
			{ 
				SendReply(player, Lang("NO_ACCESS", player.UserIDString)); 
				return; 
			}

			if(args.Length < 1)
			{ 
				SendReply(player, Lang("SYNTAX_PASTEBACK", player.UserIDString)); 
				return; 
			}

			var loadname = args[0];
			var success = TryPlaceback(loadname, player, args.Skip(1).ToArray());

			if(success is string)
			{
				SendReply(player, (string)success);
				return;
			}

			if(lastPastes.ContainsKey(player.UserIDString)) 
				lastPastes.Remove(player.UserIDString);

			lastPastes.Add(player.UserIDString, (List<BaseEntity>)success);

			SendReply(player, Lang("PASTEBACK_SUCCESS", player.UserIDString));
		}

		[ChatCommand("undo")]
		private void cmdChatUndo(BasePlayer player, string command, string[] args)
		{
			if(!HasAccess(player, undoPermission)) 
			{ 
				SendReply(player, Lang("NO_ACCESS", player.UserIDString));
				return; 
			}

			if(!lastPastes.ContainsKey(player.UserIDString)) 
			{ 
				SendReply(player, Lang("NO_PASTED_STRUCTURE", player.UserIDString)); 
				return; 
			}

			foreach(var entity in lastPastes[player.UserIDString])
			{
				if(entity == null || entity.IsDestroyed) 
					continue;
				
				entity.Kill();
			}

			lastPastes.Remove(player.UserIDString);

			SendReply(player, Lang("UNDO_SUCCESS", player.UserIDString));
		}

		//Languages phrases 

		private readonly Dictionary<string, Dictionary<string, string>> messages = new Dictionary<string, Dictionary<string, string>> 
		{	
			{"FILE_NOT_EXISTS", new Dictionary<string, string>() {
				{"en", "File does not exist"},
				{"ru", "Файл не существует"},
			}},			
			{"FILE_EMPTY", new Dictionary<string, string>() {
				{"en", "File is empty"},
				{"ru", "Файл пустой"},
			}},	
			{"NO_ACCESS", new Dictionary<string, string>() {
				{"en", "You don't have the permissions to use this command"},
				{"ru", "У вас нет прав доступа к данной команде"},
			}},			
			{"SYNTAX_PASTEBACK", new Dictionary<string, string>() {
				{"en", "Syntax: /placeback TARGETFILENAME options values\nheight XX - Adjust the height\ncheckplaced true/false - checks if parts of the house are already placed or not, if they are already placed, the building part will be removed"},
				{"ru", "Синтаксис: /placeback НАЗВАНИЕОБЪЕКТА опция значение\nheight XX - Высота от земли\ncheckplaced true/false - если часть здания уже вставлена, то объект будет пропущен"},
			}},		
			{"SYNTAX_PASTE_OR_PASTEBACK", new Dictionary<string, string>() {
				{"en", "Syntax: /paste or /placeback TARGETFILENAME options values\nheight XX - Adjust the height\nautoheight true/false - sets best height, carefull of the steep\ncheckplaced true/false - checks if parts of the house are already placed or not, if they are already placed, the building part will be removed\nblockcollision XX - blocks the entire paste if something the new building collides with something\ndeployables true/false - false to remove deployables\ninventories true/false - false to ignore inventories"},
				{"ru", "Синтаксис: /paste or /placeback НАЗВАНИЕОБЪЕКТА опция значение\nheight XX - Высота от земли\nautoheight true/false - автоматически подобрать высоту от земли\ncheckplaced true/false - если часть здания уже вставлена, то объект будет пропущен\nblockcollision XX - блокировать вставку, если что-то этому мешает\ndeployables true/false - false для удаления предметов\ninventories true/false - false для игнорирования копирования инвентаря"},
			}},		
			{"PASTEBACK_SUCCESS", new Dictionary<string, string>() {
				{"en", "You've successfully placed back the structure"},
				{"ru", "Постройка успешно вставлена на старое место"},
			}},		
			{"PASTE_SUCCESS", new Dictionary<string, string>() {
				{"en", "You've successfully pasted the structure"},
				{"ru", "Постройка успешно вставлена"},
			}},		
			{"SYNTAX_COPY", new Dictionary<string, string>() {
				{"en", "Syntax: /copy TARGETFILENAME options values\n radius XX (default 3)\n mechanics proximity/building (default building)\nbuilding true/false (saves structures or not)\ndeployables true/false (saves deployables or not)\ninventories true/false (saves inventories or not)"},
				{"ru", "Синтаксис: /copy НАЗВАНИЕОБЪЕКТА опция значение\n radius XX (default 3)\n mechanics proximity/building (по умолчанию building)\nbuilding true/false (сохранять постройку или нет)\ndeployables true/false (сохранять предметы или нет)\ninventories true/false (сохранять инвентарь или нет)"},
			}},		
			{"NO_ENTITY_RAY", new Dictionary<string, string>() {
				{"en", "Couldn't ray something valid in front of you"},
				{"ru", "Не удалось найти какой-либо объект перед вами"},
			}},		
			{"COPY_SUCCESS", new Dictionary<string, string>() {
				{"en", "The structure was successfully copied as {0}"},
				{"ru", "Постройка успешно скопирована под названием: {0}"},
			}},		
			{"NO_PASTED_STRUCTURE", new Dictionary<string, string>() {
				{"en", "You must paste structure before undoing it"},
				{"ru", "Вы должны вставить постройку перед тем, как отменить действие"},
			}},		
			{"UNDO_SUCCESS", new Dictionary<string, string>() {
				{"en", "You've successfully undid what you pasted"},
				{"ru", "Вы успешно снесли вставленную постройку"},
			}},			 
			{"NOT_FOUND_PLAYER", new Dictionary<string, string>() {
				{"en", "Couldn't find the player"},
				{"ru", "Не удалось найти игрока"},
			}}	
		};
	}
}