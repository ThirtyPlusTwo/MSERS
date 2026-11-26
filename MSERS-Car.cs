string _ScriptVersion = "0.0.6";
bool _IsRegistered = false;

float _delta = 0f;
DateTime _currentTimeStamp;
DateTime _lastTimeStamp;
float _connectionTimeout = 0f;
float _ConnectionTimeoutLength = 2f; // Time in seconds before script checks for address registration

MyIni _SetupIni = new MyIni();
MyIni _IGCIni = new MyIni();

ErrorMessage _SetupErrors = new ErrorMessage(0,"");
IMyShipController _MainController;

List<IMyGyro> _Gyros = new List<IMyGyro>();
bool _DoGyroActions = false;
float _GyroRPM = 0f;
float _StabGyroPower = 0f;
int _StabGyroReenableDelay = 0;

List<IMyThrust> _Thrusters = new List<IMyThrust>();
Suspensions _Suspensions;
TrackedRacer _Racer;

CarClass _CurrentClass;
Dictionary<string, TireClass> _TireClasses = new Dictionary<string, TireClass>();

IMyBroadcastListener _broadcastListener;
IMyUnicastListener _unisource;
long _controlAddress = -1;

float[] _FrontSteeringPoints;
float[] _RearSteeringPoints;

public Program() {
	Me.GetSurface(0).ContentType = ContentType.TEXT_AND_IMAGE;
	Me.GetSurface(0).WriteText("");
	Me.CustomName = $"PB MSERS-Car v{_ScriptVersion}";

	if (Me.CustomData.Trim() == "") { _SetupErrors.Increment("Missing Setup INI in CustomData.\n\n"); }

	MyIniParseResult result;
	if (!_SetupIni.TryParse(ParseCustomData(Me.CustomData), out result)) { _SetupErrors.Increment($"CustomData Error at {result}\n\n"); }

	if (HandleErrors(_SetupErrors)) { return; }

	try {
		SetupControlSeat();
		SetupSuspensions();
		SetupThrusters();
		SetupNamingSettings();
		SetupSteeringSettings();

		SetupGyroSettings();
		SetupGyros();

		SetupCommunicationsListeners();
	} catch (Exception e) {
		_SetupErrors.Increment($"{e.Message}");
	}
	if (HandleErrors(_SetupErrors)) { return; }

	Runtime.UpdateFrequency = UpdateFrequency.Update1;
}

public void Main(string argument) {
	if (HandleErrors(_SetupErrors)) { return; }

	_currentTimeStamp = DateTime.Now;
	_delta = (float)(_currentTimeStamp - _lastTimeStamp).TotalSeconds;
	_lastTimeStamp = _currentTimeStamp;

	HandleArgument(argument);
	UpdateCommunication();
	UpdateSteering();
	UpdateGyros();

	HandleEchoStates();
}

void UpdateGyros() {
	if (!_DoGyroActions) { return; }
}

void HandleArgument(string argument) {
	argument = argument.Trim().ToUpper();
}

void SetupCommunicationsListeners() {
	_unisource = IGC.UnicastListener;
	_broadcastListener = IGC.RegisterBroadcastListener("Address");
}

void SetupSteeringSettings() {
	string curSec = "STEERING";

	_FrontSteeringPoints = GetFloatListFromString(_SetupIni.Get(curSec, "FrontSteeringPoints").ToString());
	_RearSteeringPoints = GetFloatListFromString(_SetupIni.Get(curSec, "RearSteeringPoints").ToString());
}

void UpdateSteering() {
	if (_CurrentClass == null) { return; }

	if (!_CurrentClass.DoFrontSteering && (_Suspensions.FL.Steering || _Suspensions.FR.Steering)) {
		_Suspensions.FL.MaxSteerAngle = 0f;
		_Suspensions.FR.MaxSteerAngle = 0f;
		_Suspensions.FL.Steering = false;
		_Suspensions.FR.Steering = false;
	}
	if (!_CurrentClass.DoRearSteering && (_Suspensions.RL.Steering || _Suspensions.RR.Steering)) {
		_Suspensions.RL.MaxSteerAngle = 0f;
		_Suspensions.RR.MaxSteerAngle = 0f;
		_Suspensions.RL.Steering = false;
		_Suspensions.RR.Steering = false;
	}
	
	float carSpeed = (float)_MainController.GetShipSpeed();

	float a = -( (1 - _CurrentClass.SteeringFrictionAdjustmentFactor) / (float)Math.Pow((double)(_CurrentClass.SteeringFrictionStart - 40f), 4f) );
	float x = _Suspensions.FL.Friction - _CurrentClass.SteeringFrictionStart;

	float frictionFactor = a*(float)Math.Pow((double)x, 4) + 1;
	frictionFactor = (float)MathHelper.Clamp(frictionFactor, _CurrentClass.SteeringFrictionAdjustmentFloor, 1f);
	frictionFactor = (_Suspensions.FL.Friction < _CurrentClass.SteeringFrictionStart) ? frictionFactor : 1f;

	if (_CurrentClass.DoFrontSteering) {
		if (!_Suspensions.FL.Steering || !_Suspensions.FR.Steering) {
			_Suspensions.FL.Steering = true;
			_Suspensions.FR.Steering = true;
		}

		float frontAngle = MathHelper.ToRadians(RangedInterpolation2D(carSpeed, _FrontSteeringPoints));
		frontAngle = (float)MathHelper.Clamp(frontAngle * frictionFactor, 0f, MathHelper.ToRadians(_CurrentClass.MaxSteeringAngle));

		_Suspensions.FL.MaxSteerAngle = frontAngle;
		_Suspensions.FR.MaxSteerAngle = frontAngle;
	}
	if (_CurrentClass.DoRearSteering) {
		if (!_Suspensions.RL.Steering || !_Suspensions.RR.Steering) {
			_Suspensions.RL.Steering = true;
			_Suspensions.RR.Steering = true;
		}

		float rearAngle = MathHelper.ToRadians(RangedInterpolation2D(carSpeed, _RearSteeringPoints));
		rearAngle = (float)MathHelper.Clamp(rearAngle * frictionFactor, 0f, MathHelper.ToRadians(_CurrentClass.MaxSteeringAngle));

		_Suspensions.RL.MaxSteerAngle = rearAngle;
		_Suspensions.RR.MaxSteerAngle = rearAngle;
	}
}

void SetupThrusters() {
	// todo
}

void SetupGyroSettings() {
	string curSec = "GYRO";

	bool doGyroActions = _SetupIni.Get(curSec, "DoGyroActions").ToBoolean();
	float gyroRPM = _SetupIni.Get(curSec, "MaxGyroRPM").ToSingle();
	float stabGyroPower = _SetupIni.Get(curSec, "StabGyroPower").ToSingle();
	int stabGyroReenableDelay = (int)(_SetupIni.Get(curSec, "StabGyroReenableDelay").ToSingle() * 1000);

	_DoGyroActions = doGyroActions;
	_GyroRPM = gyroRPM;
	_StabGyroPower = stabGyroPower;
	_StabGyroReenableDelay = stabGyroReenableDelay;
}

private void SetupGyros()
{
	if (!_DoGyroActions) { return; }

    var list = new List<IMyTerminalBlock>();

    GridTerminalSystem.GetBlocksOfType<IMyGyro>(list, g => g.CubeGrid == Me.CubeGrid);

    if (list.Count < 1)
    {
        _SetupErrors.Increment("Gyro Actions requires at least 1 gyroscope.\n\n");
        return;
    }

    _Gyros = list.ConvertAll(g => (IMyGyro)g);
    for (int i = 0; i < _Gyros.Count; i++)
    {
        _Gyros[i].GyroPower = 1f;
        _Gyros[i].Enabled = true;
    }
}

void SetupNamingSettings() {
	string curSec = "NAMING";

	string gridName = _SetupIni.Get(curSec, "GridName").ToString().Replace("\"","");
	string driverName = _SetupIni.Get(curSec, "DriverName").ToString().Replace("\"","");
	int driverNumber = Math.Abs(_SetupIni.Get(curSec, "DriverNumber").ToInt32());
	string teamTag = _SetupIni.Get(curSec, "TeamTag").ToString().Replace("\"","");
	string classTag = _SetupIni.Get(curSec, "ClassTag").ToString().Replace("\"","");

	_Racer = new TrackedRacer(gridName, driverName, driverNumber, Me.CubeGrid.EntityId, teamTag, classTag);
	Me.CubeGrid.CustomName = _Racer.CubeGridName;
}

void HandleEchoStates() {
	StringBuilder outputMessage = new StringBuilder("");

	outputMessage.AppendLine($"Running MSERS-Car v{_ScriptVersion}");
	outputMessage.AppendLine($"Debug: {Runtime.CurrentInstructionCount}/{Runtime.MaxInstructionCount}");
	outputMessage.AppendLine($"Registered: {_IsRegistered}");
	outputMessage.AppendLine("");
	if (_CurrentClass != null) {
		outputMessage.AppendLine($"Current Class: {_CurrentClass.ClassTagName}");
		outputMessage.AppendLine($"F/R Steering: {_CurrentClass.DoFrontSteering}/{_CurrentClass.DoRearSteering}");
		outputMessage.AppendLine($"Min Wheelbase: {_CurrentClass.MinWheelbase} blocks");
		outputMessage.AppendLine($"Base Max Speed: {_CurrentClass.BaseMaxSpeed}m/s");
		outputMessage.AppendLine($"Base Power: {_CurrentClass.BasePower}%");
	}

	Echo(outputMessage.ToString());
}

void SetupControlSeat()
{
	var list = new List<IMyShipController>();

	GridTerminalSystem.GetBlocksOfType<IMyShipController>(list, c => c.CubeGrid == Me.CubeGrid);
	
	var control = list.FirstOrDefault(c => c is IMyRemoteControl) ?? list.FirstOrDefault(c => c is IMyCockpit);

	if (control == null)
	{
		_SetupErrors.Increment("No valid IMyShipController Component found on craft.\n\n");
		return;
	}

	_MainController = (IMyShipController)control;
}

void SetupSuspensions()
{
	var suspensions = new List<IMyMotorSuspension>();
	GridTerminalSystem.GetBlocksOfType(suspensions, s => s.CubeGrid == Me.CubeGrid);

	if (suspensions.Count != 4)
	{
		_SetupErrors.Increment("Only supports 4 suspensions.\n\n");
		return;
	}

	_Suspensions = new Suspensions();
	for (int i = 0; i < suspensions.Count; i++)
	{
		Vector3D worldDirection = suspensions[i].GetPosition() - _MainController.CenterOfMass;
		Vector3D bodyPosition = Vector3D.TransformNormal(worldDirection, MatrixD.Transpose(_MainController.WorldMatrix));

		if (bodyPosition.X < 0)
		{
			if (bodyPosition.Z < 0)
			{
				_Suspensions.FL = (IMyMotorSuspension)suspensions[i];
				_Suspensions.FL.CustomName = "Wheel Suspension FL";
			}
			else if (bodyPosition.Z > 0)
			{
				_Suspensions.RL = (IMyMotorSuspension)suspensions[i];
				_Suspensions.RL.CustomName = "Wheel Suspension RL";
			}
		}
		else if (bodyPosition.X > 0)
		{
			if (bodyPosition.Z < 0)
			{
				_Suspensions.FR = (IMyMotorSuspension)suspensions[i];
				_Suspensions.FR.CustomName = "Wheel Suspension FR";
			}
			else if (bodyPosition.Z > 0)
			{
				_Suspensions.RR = (IMyMotorSuspension)suspensions[i];
				_Suspensions.RR.CustomName = "Wheel Suspension RR";
			}
		}
	}
	_Suspensions.Wheels = new List<IMyMotorSuspension> {
		_Suspensions.FL,
		_Suspensions.FR,
		_Suspensions.RL,
		_Suspensions.RR
	};
}

void UpdateCommunication() {
	if (!_unisource.HasPendingMessage) {
		_connectionTimeout += _delta;

		if (_broadcastListener.HasPendingMessage && _connectionTimeout > _ConnectionTimeoutLength) {
			_IsRegistered = false;
			MyIGCMessage message = _broadcastListener.AcceptMessage();

			if (message.Tag.ToUpper() == "ADDRESS") {
				string returnMessage = $"[REGISTER]\nCubeGridName=\"{_Racer.CubeGridName}\"\nCleanName=\"{_Racer.CleanName}\"\nClassTag=\"{_Racer.ClassTag}\"\nDriverNumber={_Racer.DriverNumber}\nId={_Racer.Id}\nIgcAddress={IGC.Me}";
				_controlAddress = Convert.ToInt64(message.Data.ToString());
				IGC.SendUnicastMessage(_controlAddress, "REGISTER", returnMessage);
			}
		}

		return;
	}

	if (_CurrentClass == null) {
		string returnMessage = $"[REGISTER]\nCubeGridName=\"{_Racer.CubeGridName}\"\nCleanName=\"{_Racer.CleanName}\"\nClassTag=\"{_Racer.ClassTag}\"\nDriverNumber={_Racer.DriverNumber}\nId={_Racer.Id}\nIgcAddress={IGC.Me}";
		IGC.SendUnicastMessage(_controlAddress, "REGISTER-DATA", returnMessage);
	}

	while(_unisource.HasPendingMessage) {
		MyIGCMessage message = _unisource.AcceptMessage();
		_controlAddress = message.Source;
		string messageString = message.Data.ToString();

		switch(message.Tag.ToUpper()) {
			case "REGISTER-DATA":
				HandleRegisterSignal(messageString);
				break;
			case "RACE-DATA":
				HandleRaceData(messageString);
				break;
			case "ARGUMENT":
				HandleArgument(messageString);
				break;
			default:
				break;
		}
	}

	_connectionTimeout = 0f;
}

void HandlePitCommand(string message) {
	MyIniParseResult result;
	if (!_IGCIni.TryParse(message, out result)) { return; }
}

void HandleRaceData(string message) {
	MyIniParseResult result;
	if (!_IGCIni.TryParse(message, out result)) { return; }

	Me.GetSurface(0).WriteText(message);
}

void HandleRegisterSignal(string message) {
	_IsRegistered = true;

	if (_CurrentClass != null) { return; }
	if (_TireClasses.Keys.Count > 0) { return; }

	MyIniParseResult result;
	if (!_IGCIni.TryParse(message, out result)) { return; }

	SetupCarClass();
	SetupTireClasses();

	Me.GetSurface(0).WriteText(message);
}

void SetupCarClass() {
	string curSec = "CAR-CLASS";

	string classNameTag = _IGCIni.Get(curSec, "ClassNameTag").ToString().Replace("\"","");

	float baseMaxSpeed = _IGCIni.Get(curSec, "BaseMaxSpeed").ToSingle();
	float basePower = _IGCIni.Get(curSec, "BasePower").ToSingle();
	float minWheelHeight = _IGCIni.Get(curSec, "MinWheelHeight").ToSingle();
	int minWheelbase = _IGCIni.Get(curSec, "MinWheelbase").ToInt32();
	float maxLateralSpeed = _IGCIni.Get(curSec, "MaxLateralSpeed").ToSingle();
	float minWeight = _IGCIni.Get(curSec, "MinWeight").ToSingle();
	float maxWeight = _IGCIni.Get(curSec, "MaxWeight").ToSingle();

	bool doFrontSteering = _IGCIni.Get(curSec, "DoFrontSteering").ToBoolean();
	bool doRearSteering = _IGCIni.Get(curSec, "DoRearSteering").ToBoolean();
	float maxSteeringAngle = _IGCIni.Get(curSec, "MaxSteeringAngle").ToSingle();
	float steeringFrictionAdjustmentFactor = _IGCIni.Get(curSec, "SteeringFrictionAdjustmentFactor").ToSingle();
	float steeringFrictionAdjustmentFloor = _IGCIni.Get(curSec, "SteeringFrictionAdjustmentFloor").ToSingle();
	float steeringFrictionStart = _IGCIni.Get(curSec, "SteeringFrictionStart").ToSingle();

	float draftingSpeedIncrease = _IGCIni.Get(curSec, "DraftingSpeedIncrease").ToSingle();
	float draftingPowerIncrease = _IGCIni.Get(curSec, "DraftingPowerIncrease").ToSingle();
	int residualDraftingTime = (int)(_IGCIni.Get(curSec, "ResidualDraftingTime").ToSingle() * 1000);
	bool isDraftingEnabled = (draftingSpeedIncrease + draftingPowerIncrease) > 0;

	float fuelSpeedIncrease = _IGCIni.Get(curSec, "FuelSpeedIncrease").ToSingle();
	float fuelPowerIncrease = _IGCIni.Get(curSec, "FuelPowerIncrease").ToSingle();
	int fuelLifetime = (int)(_IGCIni.Get(curSec, "FuelLifetime").ToSingle() * 1000);
	int refuelTime = (int)(_IGCIni.Get(curSec, "RefuelTime").ToSingle() * 1000);
	float maxFuelModifier = _IGCIni.Get(curSec, "MaxFuelModifier").ToSingle();
	bool isFuelEnabled = (fuelLifetime) > 0;

	float ersSpeedIncrease = _IGCIni.Get(curSec, "ERSSpeedIncrease").ToSingle();
	float ersPowerIncrease = _IGCIni.Get(curSec, "ERSPowerIncrease").ToSingle();
	float ersWheelPropulsionOverride = _IGCIni.Get(curSec, "ERSWheelPropulsionOverride").ToSingle();
	int ersLifetime = (int)(_IGCIni.Get(curSec, "ERSLifetime").ToSingle() * 1000);
	int ersRechargeTime = (int)(_IGCIni.Get(curSec, "ERSRechargeTime").ToSingle() * 1000);
	int ersPitRechargeTime = (int)(_IGCIni.Get(curSec, "ERSPitRechargeTime").ToSingle() * 1000);
	float maxERSModifier = _IGCIni.Get(curSec, "MaxERSModifier").ToSingle();
	bool isERSEnabled = (ersLifetime) > 0;

	float downforceFactor = _IGCIni.Get(curSec, "DownforceFactor").ToSingle();
	float minDownforceSpeed = _IGCIni.Get(curSec, "MinDownforceSpeed").ToSingle();
	bool isDownforceEnabled = (downforceFactor) > 0;

	curSec = "PIT-ZONE";
	float pitSpeed = _IGCIni.Get(curSec, "PitSpeed").ToSingle();
	int minPitTime = (int)(_IGCIni.Get(curSec, "MinPitTime").ToSingle() * 1000);
	int maxPitTime = (int)(_IGCIni.Get(curSec, "MaxPitTime").ToSingle() * 1000);

	_CurrentClass = new CarClass(classNameTag, baseMaxSpeed, basePower, minWheelHeight, minWheelbase, maxLateralSpeed, minWeight, maxWeight, isDraftingEnabled, isFuelEnabled, isERSEnabled, isDownforceEnabled);
	_CurrentClass.SetSteeringValues(doFrontSteering, doRearSteering, maxSteeringAngle, steeringFrictionAdjustmentFactor, steeringFrictionAdjustmentFloor, steeringFrictionStart);
	_CurrentClass.SetDraftingValues(draftingSpeedIncrease, draftingPowerIncrease, residualDraftingTime);
	_CurrentClass.SetFuelValues(fuelSpeedIncrease, fuelPowerIncrease, fuelLifetime, refuelTime, maxFuelModifier);
	_CurrentClass.SetERSValues(ersSpeedIncrease, ersPowerIncrease, ersWheelPropulsionOverride, ersLifetime, ersRechargeTime, ersPitRechargeTime, maxERSModifier);
	_CurrentClass.SetDownforceValues(downforceFactor, minDownforceSpeed);
	_CurrentClass.SetPitValues(pitSpeed, minPitTime, maxPitTime);
}

void SetupTireClasses() {
	List<string> tireClassSectionNames = GetSectionNamesByPattern("TIRE-CLASS-", _IGCIni);

	foreach (string curSec in tireClassSectionNames) {
		string tireName = _IGCIni.Get(curSec, "TireName").ToString().Replace("\"","").ToUpper();
		int[] tireColors = GetIntListFromString(_IGCIni.Get(curSec, "TireColor").ToString());
		Color tireColor = new Color(tireColors[0], tireColors[1], tireColors[2]);

		float startFriction = _IGCIni.Get(curSec, "StartFriction").ToSingle();
		float endFriction = _IGCIni.Get(curSec, "EndFriction").ToSingle();
		int lifetime = (int)(_IGCIni.Get(curSec, "Lifetime").ToSingle() * 1000);
		float maxWetness = _IGCIni.Get(curSec, "MaxWetness").ToInt32();
		float wetnessEffect = _IGCIni.Get(curSec, "WetnessEffect").ToSingle();
		int wetnessTime = (int)(_IGCIni.Get(curSec, "WetnessTime").ToSingle() * 1000);

		_TireClasses.Add(tireName, new TireClass(tireName, tireColor, startFriction, endFriction, lifetime, maxWetness, wetnessEffect, wetnessTime));
	}
}

bool HandleErrors(ErrorMessage errorMessage) {
	bool returnBool = errorMessage.ErrorCount > 0;

	if (returnBool) {
		Echo("Check the Programmable Block display for a list of errors. Click on \"Edit Text\" to see the whole message.");
		Me.GetSurface(0).WriteText($"There are currently {errorMessage.ErrorCount} setup errors:\n\n{errorMessage.Message}");
	}

	return returnBool;
}

string ParseCustomData(string inputIni) {
	StringBuilder returnIni = new StringBuilder("");
	string[] lines = inputIni.Split('\n');

	for (int i = 0; i < lines.Length; i++) {
		string splitLine = lines[i].Split('#')[0].Trim();
		if (splitLine == "") { continue; }

		if (splitLine.IndexOf("{", StringComparison.InvariantCultureIgnoreCase) >= 0 && splitLine.IndexOf("}", StringComparison.InvariantCultureIgnoreCase) < 0) {
			returnIni.Append(splitLine);
			continue;
		}
		if (splitLine.IndexOf("=", StringComparison.InvariantCultureIgnoreCase) < 0 && splitLine.IndexOf(",", StringComparison.InvariantCultureIgnoreCase) >= 0) {
			returnIni.Append(splitLine);
			continue;
		}
		if (splitLine.IndexOf("=", StringComparison.InvariantCultureIgnoreCase) < 0 && splitLine.IndexOf("}", StringComparison.InvariantCultureIgnoreCase) >= 0) {
			returnIni.AppendLine(splitLine);
			continue;
		}

		returnIni.AppendLine(splitLine);
	}

	return returnIni.ToString();
}

List<string> GetSectionNamesByPattern(string pattern, MyIni ini) {
	List<string> returnList = new List<string>();
	List<string> baseList = new List<string>();
	ini.GetSections(baseList);
	for (int i = 0; i < baseList.Count; i++) {
		if (baseList[i].IndexOf(pattern, StringComparison.InvariantCultureIgnoreCase) >= 0 && baseList[i].Length >= pattern.Length) {
			returnList.Add(baseList[i]);
		}
	}
	return returnList;
}

int[] GetIntListFromString (string inputList) {
	string baseList = inputList.Trim().Replace(" ", "").Replace("{", "").Replace("}", "");
	string[] splitList = baseList.Split(',');
	int[] outputList = new int[splitList.Length];
	
	for (int i = 0; i < splitList.Length; i++) {
		Int32.TryParse(splitList[i], out outputList[i]);
	}
	
	return outputList;
}

float[] GetFloatListFromString (string inputList) {
	string baseList = inputList.Trim().Replace(" ", "").Replace("{", "").Replace("}", "");
	string[] splitList = baseList.Split(',');
	float[] outputList = new float[splitList.Length];
	
	for (int i = 0; i < splitList.Length; i++) {
		Single.TryParse(splitList[i], out outputList[i]);
	}
	
	return outputList;
}

float RangedInterpolation2D(float dependentValue, float[] coordinates) {
	float[] dependentBounds = new float[coordinates.Length/2];
	float[] independentValues = new float[coordinates.Length/2];

	int index = 0;
	for (int i = 0; i < coordinates.Length; i += 2) {
		dependentBounds[index] = coordinates[i];
		independentValues[index] = coordinates[i + 1];
		index++;
	}

    int[] axisIndexes = DetermineAxisIndices(dependentValue, dependentBounds);

    int lowerIndex = axisIndexes[0];
    int upperIndex = axisIndexes[1];

    if (lowerIndex == upperIndex) { return independentValues[lowerIndex]; }

    float slope = (independentValues[upperIndex] - independentValues[lowerIndex]) / (dependentBounds[upperIndex] - dependentBounds[lowerIndex]);
    slope = (float.IsNaN(slope) || float.IsInfinity(slope)) ? 0f : slope;
    float intercept = independentValues[lowerIndex] - slope * dependentBounds[lowerIndex];
    return slope * dependentValue + intercept; // y = m*x + b
}

int[] DetermineAxisIndices(float axisCoordinate, float[] axisCoordinates) {
    int maxAxisBound = axisCoordinates.Length - 1;

    if (axisCoordinate < axisCoordinates[0]) { return new int[] {0, 0}; }
    if (axisCoordinate > axisCoordinates[maxAxisBound]) { return new int[] {maxAxisBound, maxAxisBound}; }

    for (int i = 0; i < maxAxisBound; i++) {
        if (axisCoordinate > axisCoordinates[i + 1]) { continue; }

        return new int[] {i, i + 1};
    }

    return new int[] {0, 0};
}

private struct ErrorMessage {
	public int ErrorCount {get;set;}
	public string Message {get;set;}
	
	public ErrorMessage(int errorCount, string message) {
		ErrorCount = errorCount;
		Message = message;
	}

	public void Increment(string message) {
		ErrorCount++;
		Message += message;
	}
}

private class Suspensions {
	public IMyMotorSuspension FL {get;set;}
	public IMyMotorSuspension FR {get;set;}
	public IMyMotorSuspension RL {get;set;}
	public IMyMotorSuspension RR {get;set;}
	public List<IMyMotorSuspension> Wheels {get;set;}

	public Suspensions() {
		FL = null;
		FR = null;
		RL = null;
		RR = null;
		Wheels = new List<IMyMotorSuspension>();
	}
}

private struct TrackedRacer {
	public string GridName {get;set;}
	public string DriverName {get;set;}
	public int DriverNumber {get;set;}
	public long Id {get;set;}
	public string TeamTag {get;set;}
	public string ClassTag {get;set;}

	public string CleanName {get;set;}
	public string CubeGridName {get;set;}

	public TrackedRacer(string gridName, string driverName, int driverNumber, long id, string teamTag, string classTag) {
		GridName = gridName;
		DriverName = driverName;
		DriverNumber = driverNumber;
		Id = id;
		TeamTag = teamTag;
		ClassTag = classTag;

		CleanName = $"{TeamTag} {ClassTag} #{DriverNumber:00}-{DriverName}";
		CubeGridName = $"{GridName} {TeamTag}{ClassTag}#{DriverNumber:00} {DriverName}";
	}
}

private class CarClass {
	public string ClassTagName {get;set;}

	public float BaseMaxSpeed {get;set;}
	public float BasePower {get;set;}
	public int MinWheelbase {get;set;}
	public float MinWheelHeight {get;set;}
	public float MinWeight {get;set;}
	public float MaxWeight {get;set;}
	public float MaxLateralSpeed {get;set;}

	public bool DoFrontSteering {get;set;}
	public bool DoRearSteering {get;set;}
	public float MaxSteeringAngle {get;set;}
	public float SteeringFrictionAdjustmentFactor {get;set;}
	public float SteeringFrictionAdjustmentFloor {get;set;}
	public float SteeringFrictionStart {get;set;}
	
	public bool IsDraftingEnabled {get;set;}
	public float DraftingSpeedIncrease {get;set;}
	public float DraftingPowerIncrease {get;set;}
	public int ResidualDraftingTime {get;set;}
	
	public bool IsFuelEnabled {get;set;}
	public float FuelSpeedIncrease {get;set;}
	public float FuelPowerIncrease {get;set;}
	public int FuelLifetime {get;set;}
	public int RefuelTime {get;set;}
	public float MaxFuelModifier {get;set;}
	
	public bool IsERSEnabled {get;set;}
	public float ERSSpeedIncrease {get;set;}
	public float ERSPowerIncrease {get;set;}
	public float ERSWheelPropulsionOverride {get;set;}
	public int ERSLifetime {get;set;}
	public int ERSRechargeTime {get;set;}
	public int ERSPitRechargeTime {get;set;}
	public float MaxERSModifier {get;set;}
	
	public bool IsDownforceEnabled {get;set;}
	public float DownforceFactor {get;set;}
	public float MinDownforceSpeed {get;set;}

	public float PitSpeed {get;set;}
	public int MinPitTime {get;set;}
	public int MaxPitTime {get;set;}
	
	public CarClass(string classTagName, float baseMaxSpeed, float basePower, float minWheelHeight, int minWheelbase, float maxLateralSpeed, float minWeight, float maxWeight, bool isDraftingEnabled, bool isFuelEnabled, bool isERSEnabled, bool isDownforceEnabled) {
		ClassTagName = classTagName;

		BaseMaxSpeed = baseMaxSpeed;
		BasePower = basePower;
		MinWheelHeight = minWheelHeight;
		MinWheelbase = minWheelbase;
		MaxLateralSpeed = maxLateralSpeed;
		MinWeight = minWeight;
		MaxWeight = maxWeight;
		
		IsDraftingEnabled = isDraftingEnabled;
		IsFuelEnabled = isFuelEnabled;
		IsERSEnabled = isERSEnabled;
		IsDownforceEnabled = isDownforceEnabled;
	}

	public void SetSteeringValues(bool doFrontSteering, bool doRearSteering, float maxSteeringAngle, float steeringFrictionAdjustmentFactor, float steeringFrictionAdjustmentFloor, float steeringFrictionStart) {
		if (!doFrontSteering && !doRearSteering) { return; }

		DoFrontSteering = doFrontSteering;
		DoRearSteering = doRearSteering;
		MaxSteeringAngle = maxSteeringAngle;
		SteeringFrictionAdjustmentFactor = steeringFrictionAdjustmentFactor;
		SteeringFrictionAdjustmentFloor = steeringFrictionAdjustmentFloor;
		SteeringFrictionStart = steeringFrictionStart;
	}
	
	public void SetDraftingValues(float draftingSpeedIncrease, float draftingPowerIncrease, int residualDraftingTime) {
		if (!IsDraftingEnabled) { return; }
		
		DraftingSpeedIncrease = draftingSpeedIncrease;
		DraftingPowerIncrease = draftingPowerIncrease;
		ResidualDraftingTime = residualDraftingTime;
	}
	
	public void SetFuelValues(float fuelSpeedIncrease, float fuelPowerIncrease, int fuelLifetime, int refuelTime, float maxFuelModifier) {
		if (!IsFuelEnabled) { return; }
		
		FuelSpeedIncrease = fuelSpeedIncrease;
		FuelPowerIncrease = fuelPowerIncrease;
		FuelLifetime = fuelLifetime;
		RefuelTime = refuelTime;
		MaxFuelModifier = maxFuelModifier;
	}
	
	public void SetERSValues(float ersSpeedIncrease, float ersPowerIncrease, float ersWheelPropulsionOverride, int ersLifetime, int ersRechargeTime, int ersPitRechargeTime, float maxERSModifier) {
		ERSSpeedIncrease = ersSpeedIncrease;
		ERSPowerIncrease = ersPowerIncrease;
		ERSWheelPropulsionOverride = ersWheelPropulsionOverride;
		ERSLifetime = ersLifetime;
		ERSRechargeTime = ersRechargeTime;
		ERSPitRechargeTime = ersPitRechargeTime;
		MaxERSModifier = maxERSModifier;
	}
	
	public void SetDownforceValues(float downforceFactor, float minDownforceSpeed) {
		DownforceFactor = downforceFactor;
		MinDownforceSpeed = minDownforceSpeed;
	}

	public void SetPitValues(float pitSpeed, int minPitTime, int maxPitTime) {
		PitSpeed = pitSpeed;
		MinPitTime = minPitTime;
		MaxPitTime = maxPitTime;
	}
}

private struct TireClass {
	public string TireName {get;set;}
	public Color TireColor {get;set;}
	public float StartFriction {get;set;}
	public float EndFriction {get;set;}
	public int Lifetime {get;set;} // int b/c ms
	public float MaxWetness {get;set;}
	public float WetnessEffect {get;set;}
	public int WetnessTime {get;set;} // int b/c ms
	
	public TireClass(string tireName, Color tireColor, float startFriction, float endFriction, int lifetime, float maxWetness, float wetnessEffect, int wetnessTime) {
		TireName = tireName;
		TireColor = tireColor;
		StartFriction = startFriction;
		EndFriction = endFriction;
		Lifetime = lifetime;
		MaxWetness = maxWetness;
		WetnessEffect = wetnessEffect;
		WetnessTime = wetnessTime;
	}
}
