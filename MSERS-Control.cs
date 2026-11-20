string _ScriptVersion = "0.0.4";

int _BroadcastCooldownTime = 1000; // Time in ms (int is more efficient than float, double, long)
int _TimingLCDsRefreshCooldownTime = 15000; // Time in ms

int _WeatherCycleCooldownTime = 100000; // Time in ms; defined in ini as well
int _MaxWeatherChangeStates = 1; // +/- this amount; defined in ini as well

int DISPLAY_WIDTH = 48;
Random rand = new Random();

long _currentTime = 0;
long _previousTime = 0;
double _delta = 0;

MyIni _SetupIni = new MyIni();
MyIni _IGCIni = new MyIni();

ErrorMessage _SetupErrors = new ErrorMessage(0, "");

List<MyDetectedEntityInfo> _DetectedEntities = new List<MyDetectedEntityInfo>();
Dictionary<int, IMySensorBlock> _Checkpoints = new Dictionary<int, IMySensorBlock>();
IMySensorBlock _PitEntry;
IMySensorBlock _PitExit;

List<TrackedRacer> _LapsLogList = new List<TrackedRacer>();
List<TrackedRacer> _RacePositionsList = new List<TrackedRacer>();
List<TrackedRacer> _FastestLapsPositionsList = new List<TrackedRacer>();
List<TrackedRacer> _displayList = new List<TrackedRacer>();

List<IMyTextPanel> _RacePositionLCDs = new List<IMyTextPanel>();
List<IMyTextPanel> _RaceLapsLCDs = new List<IMyTextPanel>();
List<IMyTextPanel> _FastestPositionLCDs = new List<IMyTextPanel>();
List<IMyTextPanel> _WeatherDetailsLCDs = new List<IMyTextPanel>();

string _IndividualTimingLCDName = "";
List<IMyTextPanel> _TimingLCDs = new List<IMyTextPanel>();

Dictionary<int, List<IMyLightingBlock>> _StartLights = new Dictionary<int, List<IMyLightingBlock>>();
List<IMyLightingBlock> _GoLights = new List<IMyLightingBlock>();
float _TimePerLight = 0f;
float _StartLightsDelayTime = 0f;
double _StartLightsOutMin = 0;
double _StartLightsOutMax = 0;
float _RandLightsTime = -1.0f;

TrackMode _CurrentTrackMode;
Dictionary<string, TrackMode> _TrackModes = new Dictionary<string, TrackMode>();
Dictionary<string, IniTransfer> _CarClasses = new Dictionary<string, IniTransfer>();
Dictionary<string, IniTransfer> _TireClasses = new Dictionary<string, IniTransfer>();

Dictionary<string, TrackedRacer> _TrackedRacers = new Dictionary<string, TrackedRacer>();

int _broadcastCooldown = 0;
int _timingLCDsCooldown = 0;
int _weatherCycleCooldown = 0;

int _weatherStateRoll = 0;
int _weatherNumChange = 0;
int _drierChance = 0;
int _wetterChance = 0;

// Weather Fading Based on existing weather.
// Item1 = Weather Subtype ID (string)
// Item2 = Weather Radius(float)
// Item3 = Weather Position(Vector3D)
// Item4 = Weather Velocity(Vector3)
// Item5 = Weather Fade In Time(int)
// Item6 = Weather Intensity(float)
private Dictionary<WeatherLevel, MyTuple<string, float, Vector3D, Vector3, int, float>> _InGameWeatherValues = new Dictionary<WeatherLevel, MyTuple<string, float, Vector3D, Vector3, int, float>>();

public Program() {
	Me.GetSurface(0).ContentType = ContentType.TEXT_AND_IMAGE;
	Me.GetSurface(0).WriteText("");
	Me.CustomName = $"PB MSERS-Control v{_ScriptVersion}";

	if (Me.CustomData.Trim() == "") { _SetupErrors.Increment("Missing Setup INI in CustomData.\n\n"); }
	MyIniParseResult result;
	if (!_SetupIni.TryParse(ParseCustomData(Me.CustomData), out result)) { _SetupErrors.Increment($"CustomData Error at {result}\n\n"); }

	List<IMyRadioAntenna> antennas = new List<IMyRadioAntenna>();
	GridTerminalSystem.GetBlocksOfType(antennas);
	if (antennas.FirstOrDefault() == null) { _SetupErrors.Increment("No antenna(s) found on the grid.\n\n"); }

	if (HandleErrors(_SetupErrors)) { return; }
	
	try {
		SetupCheckpoints();
		SetupPitSensors();
		SetupLCDs();
		SetupStartLights();
		SetupWeatherSettings();
		SetupTrackModes();
		SetupTireClasses();
		SetupCarClasses();
	} catch (Exception e) {
		_SetupErrors.Increment($"{e.Message}");
	}
	if (HandleErrors(_SetupErrors)) { return; }

	Runtime.UpdateFrequency = UpdateFrequency.Update1;
}

public void Main(string argument) {
	if (HandleErrors(_SetupErrors)) { return; }

	_currentTime = DateTime.Now.Ticks;
	_delta = new TimeSpan(_currentTime - _previousTime).TotalSeconds;
	_previousTime = _currentTime;

	HandleArgument(argument);

	UpdateLapsLists();
	UpdateLapsLCDs();
	UpdateFastestLapsLCDs();
	UpdatePositionLCDs();
	UpdateTimingLCDs();

	DoAddressBroadcast();
	DoSignalListen();
	HandleModeState();
	UpdateCommunication();

	// Placed here to make the Runtime.CurrentInstructionCount give a correct value
	HandleEchoStates();
}

void UpdateTimingLCDs() {
	_timingLCDsCooldown -= (int)Runtime.TimeSinceLastRun.TotalMilliseconds;

	if (_timingLCDsCooldown <= 0) {
		_TimingLCDs.Clear();
		GridTerminalSystem.GetBlocksOfType<IMyTextPanel>(_TimingLCDs, l => l.CustomName.Contains(_IndividualTimingLCDName));
		_timingLCDsCooldown = _TimingLCDsRefreshCooldownTime;
	}
	if (_TimingLCDs.Count <= 0) { return; }

	StringBuilder outputMessage = new StringBuilder("");
	TrackedRacer currentRacer;
	
	long theoryBestTime = 0;
	KeyValuePair<int, TimeSpan>[] bestSectors = new KeyValuePair<int, TimeSpan>[_Checkpoints.Keys.Count];

	foreach (IMyTextPanel currentLCD in _TimingLCDs) {
		if (currentLCD == null) { continue; }
		if (currentLCD.CustomData.Trim() == "") { continue; }

		currentLCD.ContentType = ContentType.TEXT_AND_IMAGE;
		currentLCD.Alignment = TextAlignment.LEFT;
		currentLCD.Font = "Monospace";
		currentLCD.FontSize = 0.885f;
		currentLCD.TextPadding = 2.0f;

		outputMessage.Clear();
		currentRacer = null;
		theoryBestTime = 0;
		bestSectors = new KeyValuePair<int, TimeSpan>[_Checkpoints.Keys.Count];
		
		bool isValid = false;
		if (_TrackedRacers.ContainsKey(currentLCD.CustomData.Trim())) {
			isValid = true;
			currentRacer = _TrackedRacers[currentLCD.CustomData.Trim()];
		}
		if (!isValid && currentRacer == null) {
			int numValue = 0;
			if (!int.TryParse(currentLCD.CustomData.Trim(), out numValue)) {} else {
				currentRacer = _TrackedRacers.Values
					.Where(r => r.DriverNumber == numValue)
					.OrderByDescending(r => r.Laps.Count)
					.FirstOrDefault();
				if (currentRacer != null) { isValid = true; }
			}
		}
		if (!isValid) {
			outputMessage.AppendLine("Number or RacerID not found.");
			currentLCD.WriteText(outputMessage.ToString());
			continue;
		}

		outputMessage.AppendLine($"{currentRacer.CleanName}\n");
		if (currentRacer.BestLap == null) {
			outputMessage.AppendLine("No laps logged.");
			currentLCD.WriteText(outputMessage.ToString());
			continue;
		}

		TimeSpan bestLapTime = currentRacer.BestLap.LapTime;
		string bestLapTimeFormatted = $"{bestLapTime.Minutes:00}:{bestLapTime.Seconds:00}.{bestLapTime.Milliseconds:000}";
		outputMessage.AppendLine($"Best Lap:    L{currentRacer.BestLapNum:00} ({bestLapTimeFormatted})");
		for (int i = 1; i <= _Checkpoints.Keys.Count; i++) {
			outputMessage.AppendLine($"S{i}:                  {currentRacer.BestLap.GetSector(i).TotalSeconds:00.000}s");
			
			bestSectors[i - 1] = currentRacer.Laps
				.Where(l => l.IsFinished)
				.Select((l, idx) => new KeyValuePair<int, TimeSpan>(idx, l.GetSector(i)))
				.Where(x => x.Value != TimeSpan.Zero)
				.OrderBy(x => x.Value)
				.FirstOrDefault();
			
			theoryBestTime += bestSectors[i - 1].Value.Ticks;
		}
		outputMessage.AppendLine("");

		TimeSpan theoryBestTimeSpan = new TimeSpan(theoryBestTime);
		string theoryBestFormatted = $"{theoryBestTimeSpan.Minutes:00}:{theoryBestTimeSpan.Seconds:00}.{theoryBestTimeSpan.Milliseconds:000}";
		outputMessage.AppendLine($"Theory Best Lap: ({theoryBestFormatted})");
		for (int j = 0; j < bestSectors.Length; j++) {
			outputMessage.AppendLine($"Best S{j + 1}:         L{(bestSectors[j].Key + 1):00} {bestSectors[j].Value.TotalSeconds:00.000}s");
		}
		TimeSpan timeImprovement = new TimeSpan(bestLapTime.Ticks - theoryBestTime);
		outputMessage.AppendLine($"Gain:               -{timeImprovement.TotalSeconds:00.000}s\n");
		
		outputMessage.AppendLine($"Complete Laps: {currentRacer.Laps.Where(l => l.IsFinished).ToList().Count}");

		currentLCD.WriteText(outputMessage);
	}
}

void UpdateLapsLists() {
	_LapsLogList = _TrackedRacers.Values
		.Where(r => r.Laps.Count > 0)
		.OrderBy(r => r.CubeGridName)
		.ToList();
	
	_RacePositionsList = _LapsLogList
		.OrderByDescending(r => r.Laps.Count)
		.ThenBy(r => r.TotalCompletedRaceTime)
		.ToList();
	
	_FastestLapsPositionsList = _LapsLogList
		.Where(r => r.BestLap != null)
		.OrderBy(r => r.BestLap.LapTime)
		.ToList();
}

void UpdateFastestLapsLCDs() {
	if (_FastestPositionLCDs.Count <= 0) { return; }

	_displayList = _FastestLapsPositionsList;
	
	TimeSpan overallFastestLap = new TimeSpan();
	if (_displayList.Count > 0) {
		overallFastestLap = _displayList.FirstOrDefault().BestLap.LapTime;
	}
	
	StringBuilder outputMessage = new StringBuilder("- Fastest Laps -\n\n");
	TrackedRacer currentRacer;
	for (int i = 0; i < _displayList.Count; i++) {
		currentRacer = _displayList[i];

		TimeSpan bestLapTime = currentRacer.BestLap.LapTime;
		string bestLapTimeFormatted = $"{bestLapTime.Minutes:00}:{bestLapTime.Seconds:00}.{bestLapTime.Milliseconds:000}";

		string racerName = TruncateString(currentRacer.CleanName.Trim(), 29).Trim();
		string line = BuildLine($"P{(i + 1):00}> {racerName}", $"L{currentRacer.BestLapNum:00} ({bestLapTimeFormatted})");
		outputMessage.AppendLine(line);

		outputMessage.Append("└► (");
		for (int k = 1; k <= _Checkpoints.Keys.Count; k++) {
			outputMessage.Append($"{currentRacer.BestLap.GetSector(k).TotalSeconds:00.000} | ");
		}
		outputMessage.Remove(outputMessage.Length - 3, 3);

		double deltas = bestLapTime.TotalSeconds - overallFastestLap.TotalSeconds;
		string deltasString = deltas > 0 ? $"+{deltas:00.000}" : "       ";

		outputMessage.Append($") {deltasString}\n");
	}

	for (int j = 0; j < _FastestPositionLCDs.Count; j++) {
		IMyTextPanel currentLCD = (IMyTextPanel)_FastestPositionLCDs[j];
		currentLCD.WriteText(outputMessage.ToString());
	}
}

void UpdatePositionLCDs() {
	if (_RacePositionLCDs.Count <= 0) { return; }

	_displayList = _RacePositionsList;
	
	StringBuilder outputMessage = new StringBuilder("- Race Positions -\n\n");
	TrackedRacer currentRacer;
	for (int i = 0; i < _displayList.Count; i++) {
		currentRacer = _displayList[i];

		TimeSpan completedRaceTime = currentRacer.TotalCompletedRaceTime;

		string racerName = TruncateString(currentRacer.CleanName.Trim(), 24).Trim();
		string completedFormatted = $"{completedRaceTime.Minutes:00}:{completedRaceTime.Seconds:00}.{completedRaceTime.Milliseconds:000}";
		string line = BuildLine($"P{(i + 1):00}> {racerName}", $"L{currentRacer.Laps.Count:00} ({completedFormatted})");
		outputMessage.AppendLine(line);
	}

	for (int j = 0; j < _RacePositionLCDs.Count; j++) {
		IMyTextPanel currentLCD = (IMyTextPanel)_RacePositionLCDs[j];
		currentLCD.WriteText(outputMessage.ToString());
	}
}

void UpdateLapsLCDs() {
	if (_RaceLapsLCDs.Count <= 0) { return; }

	_displayList = _LapsLogList;
	
	StringBuilder outputMessage = new StringBuilder("Racer,Lap,LapTime (s),");
	for (int l = 1; l <= _Checkpoints.Keys.Count; l++) {
		outputMessage.Append($"S{l}Time (s),");
	}
	outputMessage.Remove(outputMessage.Length - 1, 1);
	outputMessage.AppendLine("");
	
	TrackedRacer currentRacer;
	for (int i = 0; i < _displayList.Count; i++) {
		currentRacer = _displayList[i];

		for (int j = 0; j < currentRacer.Laps.Count; j++) {
			Lap currentLap = currentRacer.Laps[j];
			int lapCount = j + 1;
			outputMessage.Append($"{currentRacer.CubeGridName},{lapCount},{currentLap.LapTime.TotalSeconds:00.000},");
			for (int h = 1; h <= _Checkpoints.Keys.Count; h++) {
				outputMessage.Append($"{currentLap.GetSector(h).TotalSeconds:00.000},");
			}
			outputMessage.Remove(outputMessage.Length - 1, 1);
			outputMessage.AppendLine("");
		}
	}

	for (int k = 0; k < _RaceLapsLCDs.Count; k++) {
		IMyTextPanel currentLCD = (IMyTextPanel)_RaceLapsLCDs[k];
		currentLCD.WriteText(outputMessage.ToString());
	}
}

void HandleModeState() {
	if (!_CurrentTrackMode.ModeStarted) {
		ResetRacers();
		return;
	}

	if (_CurrentTrackMode.ModeStarted && _CurrentTrackMode.DoRaceLights && !_CurrentTrackMode.LightsFinished) {
		HandleRaceLights();
		return;
	}

	if (_CurrentTrackMode.MaxTime > 0 && _CurrentTrackMode.ElapsedTime < _CurrentTrackMode.MaxTime) {
		_CurrentTrackMode.ElapsedTime += _delta;
	}
	if (_CurrentTrackMode.MaxTime > 0 && _CurrentTrackMode.ElapsedTime >= _CurrentTrackMode.MaxTime && !_CurrentTrackMode.ModeFinished) {
		_CurrentTrackMode.ModeFinished = true;
	}

	UpdateWeatherState();
	UpdateIngameWeather();
	UpdateCheckpointSensors();
	UpdatePitEntrySensor();
	UpdatePitExitSensor();
}

void UpdateIngameWeather() {
	var currentState = Me.GetValue<MyTuple<string, float, Vector3D, Vector3, int, float>>("ScriptWeather");
	var currentDesiredWeather = _InGameWeatherValues[_CurrentTrackMode.CurrentWeather];
	
	if (currentState.Item6 == currentDesiredWeather.Item6) { return; }
	
	var desiredTuple = new MyTuple<string, float, Vector3D, Vector3, int, float>(
		currentDesiredWeather.Item1,
		currentDesiredWeather.Item2,
		currentDesiredWeather.Item3,
		currentDesiredWeather.Item4,
		currentDesiredWeather.Item5,
		GoToValue(currentState.Item6, currentDesiredWeather.Item6, 0.001f)
	);
	Me.SetValue("ScriptWeather", desiredTuple);
}

void UpdateWeatherState() {
	if (!_CurrentTrackMode.WeatherEnabled) { return; }
	
	_weatherCycleCooldown -= (int)Runtime.TimeSinceLastRun.TotalMilliseconds;
	if (_weatherCycleCooldown >= 0) { return; }
	
	_weatherCycleCooldown = _WeatherCycleCooldownTime;
	
	_weatherStateRoll = (int)(rand.NextDouble() * 101 );
	_weatherNumChange = (int)MathHelper.Clamp(rand.NextDouble() * (_MaxWeatherChangeStates + 1), 1, 5);
	int stateChangeMult = 0;
	
	_drierChance = 0;
	_wetterChance = 0;
	int leadLap =  _RacePositionsList.FirstOrDefault()?.Laps?.Count ?? 0;
	
	for (int i = _CurrentTrackMode.WeatherChanceValues.Length - 1; i >= 0; i -= 3) {
		int newIndex = i - 2;
		if (leadLap < _CurrentTrackMode.WeatherChanceValues[newIndex]) { continue; }

		_drierChance = _CurrentTrackMode.WeatherChanceValues[newIndex + 1];
		_wetterChance = _CurrentTrackMode.WeatherChanceValues[newIndex + 2];
		break;
	}
	
	if (_weatherStateRoll > _drierChance + _wetterChance) {
		stateChangeMult = 0; // doing nothing
	}
	if (_weatherStateRoll <= _drierChance + _wetterChance) {
		stateChangeMult = 1; // getting wetter
	}
	if (_weatherStateRoll <= _drierChance) {
		stateChangeMult = -1; // getting drier
	}
	
	
	stateChangeMult *= _weatherNumChange;
	int enumMin = Enum.GetValues(typeof(WeatherLevel)).Cast<int>().Min();
	int enumMax = Enum.GetValues(typeof(WeatherLevel)).Cast<int>().Max();
	int newState = (int)MathHelper.Clamp((int)_CurrentTrackMode.CurrentWeather + stateChangeMult, enumMin, enumMax);
	_CurrentTrackMode.CurrentWeather = (WeatherLevel)newState;
}

void HandleRaceLights() {
	if (_CurrentTrackMode.LightsFinished) { return; }

	if (_CurrentTrackMode.LightsElapsedTime <= 0f) {
		for (int k = 1; k <= _StartLights.Keys.Count; k++) {
			SetLights(k, false);
		}
		foreach (IMyLightingBlock l in _GoLights) {
			l.Enabled = true;
			l.Color = Color.Black;
		}
	}

	_CurrentTrackMode.LightsElapsedTime += _delta;

	if (_CurrentTrackMode.LightsElapsedTime < _StartLightsDelayTime) { return; }

	for (int i = 1; i <= _StartLights.Keys.Count; i++) {
		SetLights(i, ((float)_CurrentTrackMode.LightsElapsedTime - _StartLightsDelayTime) >= (i * _TimePerLight));
	}

	if (_RandLightsTime < 0f) {
		double mean = (_StartLightsOutMin + _StartLightsOutMax) / 2;
		double stddev = (mean - _StartLightsOutMin) / 3; // 3 std devs out

		double u1 = 1.0 - rand.NextDouble();
		double u2 = 1.0 - rand.NextDouble();
		double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);

		_RandLightsTime = (float)MathHelper.Clamp(mean + stddev * randStdNormal, _StartLightsOutMin, _StartLightsOutMax);
	}

	if (_CurrentTrackMode.LightsElapsedTime < (_RandLightsTime + _StartLightsDelayTime + _StartLights.Keys.Count * _TimePerLight)) { return; }

	for (int j = 1; j <= _StartLights.Keys.Count; j++) {
		SetLights(j, false);
	}
	foreach (IMyLightingBlock l in _GoLights) {
		l.Enabled = true;
		l.Color = Color.Green;
	}

	_CurrentTrackMode.LightsFinished = true;
	_RandLightsTime = -1.0f;
}

void ResetRacers() {
	if (_TrackedRacers.Keys.Count <= 0) { return; }
	_TrackedRacers = new Dictionary<string, TrackedRacer>();
}

void ResetLaps() {
	int totalLapsCount = _TrackedRacers.Values.Sum(r => r.Laps.Count);

	if (totalLapsCount > 0) {
		foreach (string key in _TrackedRacers.Keys) {
			TrackedRacer racer = _TrackedRacers[key];
			racer.ClearLaps();
		}
	}
}

void UpdateCheckpointSensors() {
	for (int i = 1; i <= _Checkpoints.Keys.Count; i++) {
		_DetectedEntities.Clear();

		_Checkpoints[i].DetectedEntities(_DetectedEntities);

		foreach (MyDetectedEntityInfo entity in _DetectedEntities) {
			if (IgnoreEntity(entity, "Grid")) { continue; }

			if (!_TrackedRacers.ContainsKey(entity.Name)) { continue; }

			TrackedRacer currentRacer = _TrackedRacers[entity.Name];

			if (entity.EntityId != currentRacer.Id) { continue; }

			if (currentRacer.CurrentLap != null) {
				currentRacer.CurrentLap.SetCheckpoint(i, DateTime.Now.Ticks);
			}
			if (i != 1) { continue; }

			if (currentRacer.CurrentLap != null) {
				if (currentRacer.Laps.Count >= _CurrentTrackMode.MaxLaps && currentRacer.CurrentLap.IsFinished && !_CurrentTrackMode.ModeFinished && _CurrentTrackMode.MaxLaps > 0) {
					_CurrentTrackMode.ModeFinished = true;
				}
			}

			if (_CurrentTrackMode.ModeFinished) { continue; }
			currentRacer.NewLap(DateTime.Now.Ticks, false);
		}
	}
}

void UpdatePitEntrySensor() {
	_DetectedEntities.Clear();
	_PitEntry.DetectedEntities(_DetectedEntities);
	foreach (MyDetectedEntityInfo entity in _DetectedEntities) {
		if (IgnoreEntity(entity, "Grid")) { continue; }

		if (!_TrackedRacers.ContainsKey(entity.Name)) { continue; }

		TrackedRacer currentRacer = _TrackedRacers[entity.Name];

		if (entity.EntityId != currentRacer.Id) { continue; }

		IGC.SendUnicastMessage(currentRacer.IgcAddress, "PIT-COMMAND", "LMT-ON");
	}
}

void UpdatePitExitSensor() {
	_DetectedEntities.Clear();
	_PitExit.DetectedEntities(_DetectedEntities);
	foreach (MyDetectedEntityInfo entity in _DetectedEntities) {
		if (IgnoreEntity(entity, "Grid")) { continue; }

		if (!_TrackedRacers.ContainsKey(entity.Name)) { continue; }

		TrackedRacer currentRacer = _TrackedRacers[entity.Name];

		if (entity.EntityId != currentRacer.Id) { continue; }

		if (currentRacer.CurrentLap != null) {
			currentRacer.CurrentLap.SetCheckpoint(1, DateTime.Now.Ticks);
			currentRacer.CurrentLap.IsPitLap = true;

			if (currentRacer.Laps.Count >= _CurrentTrackMode.MaxLaps && currentRacer.CurrentLap.IsFinished && !_CurrentTrackMode.ModeFinished && _CurrentTrackMode.MaxLaps > 0) {
				_CurrentTrackMode.ModeFinished = true;
			}
		}

		IGC.SendUnicastMessage(currentRacer.IgcAddress, "PIT-COMMAND", "LMT_OFF");

		if (_CurrentTrackMode.ModeFinished) { continue; }
		currentRacer.NewLap(DateTime.Now.Ticks, true);
	}
}

void UpdateCommunication() {
	double fastestLap = _FastestLapsPositionsList.FirstOrDefault()?.BestLap?.LapTime.TotalSeconds ?? 0;
	double fastestSectorTime = 0;
	StringBuilder fastestLapSectorData = new StringBuilder("\"");
	for (int j = 1; j <= _Checkpoints.Keys.Count; j++) {
		fastestSectorTime = _FastestLapsPositionsList.FirstOrDefault()?.BestLap?.GetSector(j).TotalSeconds ?? 0;
		fastestLapSectorData.Append($"{fastestSectorTime:00.000},");
	}
	fastestLapSectorData.Append("\"");
	fastestLapSectorData.Remove(fastestLapSectorData.Length - 2, 1);

	double timeRemaining = MathHelper.Clamp(_CurrentTrackMode.MaxTime - _CurrentTrackMode.ElapsedTime, 0, _CurrentTrackMode.MaxTime);

	foreach (string key in _TrackedRacers.Keys) {
		TrackedRacer currentRacer = _TrackedRacers[key];

		int lapCount = currentRacer.Laps?.Count ?? 0;
		double currentLap = currentRacer.CurrentLap?.LapTime.TotalSeconds ?? 0;
		double pbLap = currentRacer.BestLap?.LapTime.TotalSeconds ?? 0;
		double prevLap = currentRacer.PreviousLap?.LapTime.TotalSeconds ?? 0;
		
		StringBuilder currentSectorData = new StringBuilder("\"");
		double currentSectorTime = 0;

		StringBuilder prevSectorData = new StringBuilder("\"");
		double prevSectorTime = 0;

		StringBuilder pbSectorData = new StringBuilder("\"");
		double pbSectorTime = 0;

		for (int i = 1; i <= _Checkpoints.Keys.Count; i++) {
			currentSectorTime = currentRacer.CurrentLap?.GetSector(i).TotalSeconds ?? 0;
			currentSectorData.Append($"{currentSectorTime:00.000},");

			prevSectorTime = currentRacer.PreviousLap?.GetSector(i).TotalSeconds ?? 0;
			prevSectorData.Append($"{prevSectorTime:00.000},");

			pbSectorTime = currentRacer.BestLap?.GetSector(i).TotalSeconds ?? 0;
			pbSectorData.Append($"{pbSectorTime:00.000},");
		}
		prevSectorData.Append("\"");
		currentSectorData.Append("\"");
		pbSectorData.Append("\"");

		prevSectorData.Remove(prevSectorData.Length - 2, 1);
		currentSectorData.Remove(currentSectorData.Length - 2, 1);
		pbSectorData.Remove(pbSectorData.Length - 2, 1);

		StringBuilder raceData = new StringBuilder("");

		raceData.AppendLine("[RACE-DATA]");
		raceData.AppendLine($"Lap={lapCount}");
		raceData.AppendLine($"MaxLaps={_CurrentTrackMode.MaxLaps}");
		raceData.AppendLine($"TimeRemaining={timeRemaining}");
		raceData.AppendLine($"CurrentLapTime={currentLap:00.000}");
		raceData.AppendLine($"CurrentSectorTimes={currentSectorData.ToString()}");
		raceData.AppendLine($"PreviousLapSectorTimes={prevSectorData.ToString()}");
		raceData.AppendLine($"PBLapTime={pbLap:00.000}");
		raceData.AppendLine($"PBLapSectorTimes={pbSectorData}");
		raceData.AppendLine($"PreviousLapTime={prevLap:00.000}");
		raceData.AppendLine($"OverallFastestLap={fastestLap:00.000}");
		raceData.AppendLine($"OverallFastestLapSectorTimes={fastestLapSectorData.ToString()}");
		raceData.AppendLine($"WeatherState=\"{_CurrentTrackMode.CurrentWeather}\"");
		raceData.AppendLine($"WeatherChances=\"{_drierChance},{_wetterChance}\"");
		raceData.AppendLine($"WeatherCooldownTime={(_weatherCycleCooldown/1000.0):00.000}");

		IGC.SendUnicastMessage(currentRacer.IgcAddress, "RACE-DATA", raceData.ToString());
	}
}

void DoAddressBroadcast() {
	_broadcastCooldown -= (int)Runtime.TimeSinceLastRun.TotalMilliseconds;

	if (_broadcastCooldown >= 0) { return; }

	IGC.SendBroadcastMessage("Address", IGC.Me.ToString(), TransmissionDistance.TransmissionDistanceMax);
	_broadcastCooldown = _BroadcastCooldownTime;
}

void DoSignalListen() {
	if (!_CurrentTrackMode.ModeStarted) { return; }

	IMyUnicastListener uniListener = IGC.UnicastListener;

	while (uniListener.HasPendingMessage) {
		MyIGCMessage message = uniListener.AcceptMessage();
		string data = message.Data.ToString();

		switch (message.Tag.ToUpper()) {
			case "REGISTER":
				HandleRegisterSignal(data);
				break;
			case "ARGUMENT":
				HandleArgument(data);
				break;
			default:
				break;
		}
	}
}

void HandleRegisterSignal(string message) {
	MyIniParseResult result;
	if (!_IGCIni.TryParse(message, out result)) { return; }

	string curSec = "REGISTER";

	string cubeGridName = _IGCIni.Get(curSec, "CubeGridName").ToString().Replace("\"","");
	string cleanName = _IGCIni.Get(curSec, "CleanName").ToString().Replace("\"","");
	string classTag = _IGCIni.Get(curSec, "ClassTag").ToString().Replace("\"","");
	int driverNumber = _IGCIni.Get(curSec, "DriverNumber").ToInt32();
	long id = _IGCIni.Get(curSec, "Id").ToInt64();
	long igcAddress = _IGCIni.Get(curSec, "IgcAddress").ToInt64();

	if (cubeGridName == "" || cleanName == "" || classTag == "" || igcAddress == 0) { return; }
	if (!_CarClasses.ContainsKey(classTag)) { return; }

	if (!_TrackedRacers.ContainsKey(cubeGridName)) {
		_TrackedRacers.Add(cubeGridName, new TrackedRacer(cubeGridName, cleanName, classTag, driverNumber, id, igcAddress, _Checkpoints.Keys.Count));
	}
	if (_TrackedRacers.ContainsKey(cubeGridName)) {
		if (_TrackedRacers[cubeGridName].PreviousId != id) {
			_TrackedRacers[cubeGridName].IgcAddress = igcAddress;
			_TrackedRacers[cubeGridName].PreviousId = _TrackedRacers[cubeGridName].Id;
			_TrackedRacers[cubeGridName].Id = id;
		}
	}

	TrackedRacer currentRacer = _TrackedRacers[cubeGridName];
	IGC.SendUnicastMessage(
		currentRacer.IgcAddress,
		"REGISTER-DATA",
		_CarClasses[currentRacer.ClassTag].IniDef + $"\nPitSpeed={_SetupIni.Get("PIT-ZONE", "PitSpeed").ToString()}"
	);
}

void SetupCarClasses() {
	List<string> carClassSectionNames = GetSectionNamesByPattern("CAR-CLASS-", _SetupIni);

	foreach (string curSec in carClassSectionNames) {
		string className = _SetupIni.Get(curSec, "ClassNameTag").ToString().Replace("\"","");

		string iniDef = $"[{curSec}]\n";
		List<MyIniKey> curSecKeys = new List<MyIniKey>();
		_SetupIni.GetKeys(curSec, curSecKeys);

		foreach (MyIniKey curKey in curSecKeys) {
			iniDef += $"{curKey.Name}={_SetupIni.Get(curKey).ToString()}\n";
		}

		string[] validTires = _SetupIni.Get(curSec, "ValidTires").ToString().Replace("\"","").Split(',');

		foreach (string tire in validTires) {
			if (!_TireClasses.ContainsKey(tire.ToUpper())) { continue; }
			iniDef += $"{_TireClasses[tire.ToUpper()].IniDef}";
		}

		_CarClasses.Add(className.ToUpper(), new IniTransfer(className, iniDef));
	}
}

void SetupTireClasses() {
	List<string> tireClassSectionNames = GetSectionNamesByPattern("TIRE-CLASS-", _SetupIni);

	foreach (string curSec in tireClassSectionNames) {
		string tireName = _SetupIni.Get(curSec, "TireName").ToString().Replace("\"","");

		string iniDef = $"[{curSec}]\n";
		List<MyIniKey> curSecKeys = new List<MyIniKey>();
		_SetupIni.GetKeys(curSec, curSecKeys);

		foreach(MyIniKey curKey in curSecKeys) {
			iniDef += $"{curKey.Name}={_SetupIni.Get(curKey).ToString()}\n";
		}

		_TireClasses.Add(tireName.ToUpper(), new IniTransfer(tireName, iniDef));
	}
}

void HandleArgument(string argument) {
	// for ideally case invariance
	argument = argument.Trim().ToUpper();
	
	if (argument == "") { return; }
	
	if (_TrackModes.ContainsKey(argument)) {
		_CurrentTrackMode = _TrackModes[argument];
		return;
	}
	if (argument == "MODE") {
		_CurrentTrackMode.ModeStarted = !_CurrentTrackMode.ModeStarted;
		return;
	}
	if (argument == "MODE_ON") {
		_CurrentTrackMode.ModeStarted = true;
		return;
	}
	if (argument == "MODE_OFF") {
		_CurrentTrackMode.ModeStarted = false;
		return;
	}
	if (argument == "MODE_RESET") {
		_CurrentTrackMode.ResetMode();
		return;
	}
	WeatherLevel newWeatherState;
	if (Enum.TryParse<WeatherLevel>(argument, true, out newWeatherState)) {
		// Established like this so that weather can be set during practice too
		_CurrentTrackMode.CurrentWeather = newWeatherState;
		return;
	}
}

void HandleEchoStates() {
	StringBuilder echoMessage = new StringBuilder("");
	echoMessage.AppendLine($"Running MSERS-Control v{_ScriptVersion}");
	echoMessage.AppendLine($"Debug: {Runtime.CurrentInstructionCount}/{Runtime.MaxInstructionCount}");
	echoMessage.AppendLine("");
	echoMessage.AppendLine($"# of Lap Position LCDs: {_RacePositionLCDs.Count}");
	echoMessage.AppendLine($"# of Lap Details LCDs: {_RaceLapsLCDs.Count}");
	echoMessage.AppendLine($"# of Fastest Laps LCDs: {_FastestPositionLCDs.Count}");
	echoMessage.AppendLine($"# of Weather Details LCDs: {_WeatherDetailsLCDs.Count}");
	echoMessage.AppendLine($"# of Timing LCDs: {_TimingLCDs.Count}");
	echoMessage.AppendLine("");
	echoMessage.AppendLine($"# of Track Modes: {_TrackModes.Keys.Count}");
	echoMessage.AppendLine($"Current Track Mode: #{Array.IndexOf(_TrackModes.Keys.ToArray(), _CurrentTrackMode.Name.ToUpper())+1} {_CurrentTrackMode.Name}");
	echoMessage.AppendLine($"Mode Started: {(!_CurrentTrackMode.DoRaceLights ? "N/A" : _CurrentTrackMode.ModeStarted ? "Started" : "Not Started")}");
	echoMessage.AppendLine($"Current Weather: {_CurrentTrackMode.CurrentWeather}");
	echoMessage.AppendLine($"Weather Cooldown: {(_weatherCycleCooldown / 1000.0):00.0}s");
	echoMessage.AppendLine($"Weather Roll: {_weatherStateRoll}");
	echoMessage.AppendLine($"Weather Num Roll: {_weatherNumChange}");
	echoMessage.AppendLine($"Drier/Wetter/No Change: {_drierChance}/{_wetterChance}/{100 - _drierChance - _wetterChance}");
	echoMessage.AppendLine("");
	echoMessage.AppendLine($"# of Checkpoints: {_Checkpoints.Keys.Count}");
	echoMessage.AppendLine($"# of Start Lights: {_StartLights.Keys.Count}");
	echoMessage.AppendLine($"_RandLightsTime: {_RandLightsTime:00.000}s");
	echoMessage.AppendLine("");
	echoMessage.AppendLine($"# of Cars Registered: {_TrackedRacers.Keys.Count}");
	echoMessage.AppendLine("");
	echoMessage.AppendLine($"# of Car Classes: {_CarClasses.Keys.Count}");
	echoMessage.AppendLine($"# of Tire Compounds: {_TireClasses.Keys.Count}");
	
	Echo(echoMessage.ToString());
	Me.GetSurface(0).WriteText(echoMessage.ToString());
}

void SetupTrackModes() {
	List<string> trackModeSectionNames = GetSectionNamesByPattern("TRACK-MODE-", _SetupIni);

	foreach (string curSec in trackModeSectionNames) {
		string trackModeName = _SetupIni.Get(curSec, "TrackModeName").ToString().Replace("\"", "");
		bool doRaceLights = _SetupIni.Get(curSec, "DoRaceLights").ToBoolean();
		int maxLaps = _SetupIni.Get(curSec, "MaxLaps").ToInt32();
		double maxTime = _SetupIni.Get(curSec, "MaxTime").ToDouble();
		bool weatherEnabled = _SetupIni.Get(curSec, "WeatherEnabled").ToBoolean();

		WeatherLevel weatherInitial;
		Enum.TryParse<WeatherLevel>(_SetupIni.Get(curSec, "WeatherInitial").ToString().Replace("\"", ""), true, out weatherInitial);
		
		int weatherChanceType = _SetupIni.Get(curSec, "WeatherChanceType").ToInt32();
		string weatherChancesString = _SetupIni.Get(curSec, "WeatherChances").ToString();
		int[] weatherChances = GetIntListFromString(weatherChancesString);

		// Key is set ToUpper to make case invariance easier
		_TrackModes.Add(trackModeName.ToUpper(), new TrackMode(trackModeName, doRaceLights, maxLaps, maxTime, weatherEnabled, weatherInitial, weatherChanceType, weatherChances));
	}

	_CurrentTrackMode = _TrackModes.Values.FirstOrDefault();
}

void SetupWeatherSettings() {
	string curSec = "WEATHER";
	// x1000 to get to ms
	int weatherCycleTime = _SetupIni.Get(curSec, "WeatherCycleTime").ToInt32() * 1000;
	// This is to cap the number of states based on the number of weather states in the weather enum
	int maxWeatherChangeStates = (int)MathHelper.Clamp(_SetupIni.Get(curSec, "MaxWeatherChangeStates").ToInt32(), 1, Enum.GetNames(typeof(WeatherLevel)).Length - 1);
	
	_WeatherCycleCooldownTime = weatherCycleTime;
	_MaxWeatherChangeStates = maxWeatherChangeStates;
	
	_InGameWeatherValues = new Dictionary<WeatherLevel, MyTuple<string, float, Vector3D, Vector3, int, float>>()
	{
		[WeatherLevel.Clear] = new MyTuple<string, float, Vector3D, Vector3, int, float>("FogLight", 5000f, Me.WorldMatrix.Translation, new Vector3(0, 0, 0), 0, 0f),
		[WeatherLevel.LightClouds] = new MyTuple<string, float, Vector3D, Vector3, int, float>("FogLight", 5000f, Me.WorldMatrix.Translation, new Vector3(0, 0, 0), 0, 0.25f),
		[WeatherLevel.Cloudy] = new MyTuple<string, float, Vector3D, Vector3, int, float>("FogLight", 5000f, Me.WorldMatrix.Translation, new Vector3(0, 0, 0), 0, 0.5f),
		[WeatherLevel.Overcast] = new MyTuple<string, float, Vector3D, Vector3, int, float>("FogLight", 5000f, Me.WorldMatrix.Translation, new Vector3(0, 0, 0), 0, 0.75f),
		[WeatherLevel.Drizzle] = new MyTuple<string, float, Vector3D, Vector3, int, float>("RainLight", 5000f, Me.WorldMatrix.Translation, new Vector3(0, 1, 0), 0, 0.8f),
		[WeatherLevel.Rain] = new MyTuple<string, float, Vector3D, Vector3, int, float>("RainHeavy", 5000f, Me.WorldMatrix.Translation, new Vector3(0, 1, 0), 0, 0.9f),
		[WeatherLevel.HeavyRain] = new MyTuple<string, float, Vector3D, Vector3, int, float>("ThunderstormHeavy", 5000f, Me.WorldMatrix.Translation, new Vector3(0, 1, 0), 0, 0.99f)
	};
}

void SetupCheckpoints() {
	string curSec = "CHECKPOINTS";
	string namePattern = _SetupIni.Get(curSec, "CheckpointSensorsNamePrefix").ToString().Replace("\"", "");
	
	// Currently only supporting up to 3 sectors
	int totalCheckpoints = (int)MathHelper.Clamp(_SetupIni.Get(curSec, "TotalCheckpointCount").ToInt32(), 1, 3);
	
	for (int i = 1; i <= totalCheckpoints; i++) {
		string currentName = $"{namePattern}{i}";
		IMySensorBlock currentSensor = (IMySensorBlock)GridTerminalSystem.GetBlockWithName(currentName);
		if (currentSensor == null) {
			_SetupErrors.Increment($"IMySensorBlock with name \"{currentName}\" was not found.\n\n");
			continue;
		}
		
		_Checkpoints.Add(i, currentSensor);
	}
}

void SetupPitSensors() {
	string curSec = "PIT-ZONE";
	string pitEntryName = _SetupIni.Get(curSec, "PitEntrySensorName").ToString().Replace("\"", "");
	string pitExitName = _SetupIni.Get(curSec, "PitExitSensorName").ToString().Replace("\"", "");
	
	_PitEntry = (IMySensorBlock)GridTerminalSystem.GetBlockWithName(pitEntryName);
	_PitExit = (IMySensorBlock)GridTerminalSystem.GetBlockWithName(pitExitName);
}

void SetupLCDs() {
	string curSec = "DISPLAY";
	
	string positionLCDsName = _SetupIni.Get(curSec, "LapPositionLCDName").ToString().Replace("\"", "");
	string lapsLCDsName = _SetupIni.Get(curSec, "LapDetailsLCDName").ToString().Replace("\"", "");
	string fastLapsLCDsName = _SetupIni.Get(curSec, "FastestLapsLCDName").ToString().Replace("\"", "");
	string weatherLCDsName = _SetupIni.Get(curSec, "WeatherDetailsLCDName").ToString().Replace("\"", "");
	_IndividualTimingLCDName = _SetupIni.Get(curSec, "IndividualTimingLCDName").ToString().Replace("\"","");
	
	string[] LCDNames = {positionLCDsName, lapsLCDsName, fastLapsLCDsName, weatherLCDsName};
	List<List<IMyTextPanel>> LCDLists = new List<List<IMyTextPanel>> {_RacePositionLCDs, _RaceLapsLCDs, _FastestPositionLCDs, _WeatherDetailsLCDs};
	
	for (int i = 0; i < LCDNames.Length; i++) {
		GridTerminalSystem.GetBlocksOfType(LCDLists[i], LCD => LCD.CustomName.Contains(LCDNames[i]));
		if (LCDLists[i].Count <= 0) {
			_SetupErrors.Increment($"LCD with name \"{LCDNames[i]}\" was not found.\n\n");
		}
		
		for (int j = 0; j < LCDLists[i].Count; j++) {
			IMyTextPanel currentLCD = (IMyTextPanel)LCDLists[i][j];
			currentLCD.ContentType = ContentType.TEXT_AND_IMAGE;
			currentLCD.Alignment = TextAlignment.CENTER;
			currentLCD.Font = "Monospace";
			currentLCD.FontSize = 0.535f;
			currentLCD.TextPadding = 2.0f;
		}
	}
}

void SetupStartLights() {
	string curSec = "START-LIGHTS";

	string startLightsPattern = _SetupIni.Get(curSec, "StartLightsGroupNamePrefix").ToString().Replace("\"", "");
	string goLightsName = _SetupIni.Get(curSec, "StartLightsGoGroupName").ToString().Replace("\"", "");
	int startLightsCount = _SetupIni.Get(curSec, "StartLightsCount").ToInt32();
	_TimePerLight = _SetupIni.Get(curSec, "TimePerLight").ToSingle();
	_StartLightsDelayTime = _SetupIni.Get(curSec, "StartLightsDelayTime").ToSingle();
	_StartLightsOutMin = _SetupIni.Get(curSec, "StartLightsOutMin").ToDouble();
	_StartLightsOutMax = _SetupIni.Get(curSec, "StartLightsOutMax").ToDouble();

	for (int i = 1; i <= startLightsCount; i++) {
		string currentGroupName = $"{startLightsPattern}{i}";
		IMyBlockGroup tempBlockGroup = GridTerminalSystem.GetBlockGroupWithName(currentGroupName);
		if (tempBlockGroup == null) {
			_SetupErrors.Increment($"IMyLightingBlock Group with name \"{currentGroupName}\" was not found.\n\n");
			continue;
		}

		List<IMyLightingBlock> tempLightsList = new List<IMyLightingBlock>();
		tempBlockGroup.GetBlocksOfType(tempLightsList);
		_StartLights.Add(i, tempLightsList);
		SetLights(i, false);
	}

	IMyBlockGroup tempGoLightsGroup = GridTerminalSystem.GetBlockGroupWithName(goLightsName);
	if (tempGoLightsGroup == null) {
		_SetupErrors.Increment($"IMyLightingBlock Group with name \"{goLightsName}\" was not found.\n\n");
		return;
	}
	tempGoLightsGroup.GetBlocksOfType(_GoLights);

	foreach (IMyLightingBlock l in _GoLights) {
		if (l == null) { continue; }
		l.Enabled = true;
		l.Color = Color.Black;
	}
}

private class Lap {
	private long[] _checkpointTimeStamps { get; set; }
	public int CheckpointsLogged = 0;
	public bool IsPitLap { get; set; }

	public TimeSpan LapTime {
		get {
			long start = _checkpointTimeStamps[0];
			long end = _checkpointTimeStamps[_checkpointTimeStamps.Length - 1];
			if (end != -1) {
				return new TimeSpan(end - start);
			}

			return new TimeSpan(DateTime.Now.Ticks - start);
		}
	}

	public Lap(long startTimeStamp, int checkpointsCount, bool isPitLap = false) {
		_checkpointTimeStamps = new long[checkpointsCount + 1];

		for(int i = 0; i < _checkpointTimeStamps.Length; i++) {
			_checkpointTimeStamps[i] = -1;
		}
		
		SetCheckpoint(1, startTimeStamp);

		IsPitLap = isPitLap;
	}

	// Checkpoint 1 => 0 in array
	public void SetCheckpoint(int i, long timeStamp) {
		// ensuring the start of the lap is the first checkpoint
		if (CheckpointsLogged <= 0 && i != 1) { return; }

		// ensuring that the start finish line ends the lap
		if (CheckpointsLogged > 1 && i == 1 && _checkpointTimeStamps[_checkpointTimeStamps.Length - 1] <= 0) {
			_checkpointTimeStamps[_checkpointTimeStamps.Length - 1] = timeStamp;
			CheckpointsLogged++;
			return;
		}

		// ensuring that checkpoint times cannot be overwritten
		if (_checkpointTimeStamps[i - 1] > 0) { return; }

		_checkpointTimeStamps[i - 1] = timeStamp;
		CheckpointsLogged++;
	}

	public bool IsFinished {
		get { return CheckpointsLogged > 0 && _checkpointTimeStamps[_checkpointTimeStamps.Length - 1] > 0; }
	}

	public TimeSpan GetSector(int sectorNumber) {
		long sectorStart = _checkpointTimeStamps[sectorNumber - 1];
		long sectorEnd = _checkpointTimeStamps[sectorNumber];

		if (sectorStart <= 0 || sectorEnd <= 0) {
			return TimeSpan.Zero;
		}

		return new TimeSpan(sectorEnd - sectorStart);
	}

	public bool IsSectorFinished(int sectorNumber) {
		return GetSector(sectorNumber).TotalSeconds > 0;
	}
}

private class TrackedRacer {
	public string CubeGridName {get;set;}
	public string CleanName {get;set;}
	public string ClassTag {get;set;}
	public int DriverNumber {get;set;}
	public long Id {get;set;}
	public long PreviousId {get;set;}
	public long IgcAddress {get;set;}
	private int CheckpointsCount {get;set;}
	public List<Lap> Laps {get;private set;}

	public TrackedRacer(string cubeGridName, string cleanName, string classTag, int driverNumber, long id, long igcAddress, int checkpointsCount) {
		CubeGridName = cubeGridName;
		CleanName = cleanName;
		ClassTag = classTag;
		DriverNumber = driverNumber;
		Id = id;
		PreviousId = 0;
		IgcAddress = igcAddress;
		CheckpointsCount = checkpointsCount;

		Laps = new List<Lap>();
	}

	public Lap CurrentLap {
		get {
			return Laps.LastOrDefault();
		}
	}

	public Lap BestLap {get;private set;}

	public int BestLapNum {get;private set;}

	public Lap PreviousLap {
		get {
			if (Laps.Count <= 1) { return null;	}
			return Laps[Laps.Count - 2];
		}
	}

	public void ClearLaps() {
		Laps = new List<Lap>();
		BestLap = null;
		BestLapNum = 0;
	}

	public void NewLap(long startTimeStamp, bool isPitLap = false) {
		if (CurrentLap != null) {
			if (!CurrentLap.IsFinished) { return; }

			if (!CurrentLap.IsPitLap && CurrentLap.CheckpointsLogged == CheckpointsCount + 1 && (BestLap == null || CurrentLap.LapTime < BestLap.LapTime)) {
				BestLap = CurrentLap;
				BestLapNum = Laps.Count;
			}
		}

		Lap newLap = new Lap(startTimeStamp, CheckpointsCount, isPitLap);
		Laps.Add(newLap);
	}

	public TimeSpan TotalCompletedRaceTime {
		get {
			long totalTicks = Laps.Where(l => l.IsFinished).Sum(t => t.LapTime.Ticks);
			if (CurrentLap == null) { return new TimeSpan(totalTicks); }

			for (int i = 1; i < CheckpointsCount; i++) {
				totalTicks += CurrentLap.GetSector(i).Ticks;
			}
			return new TimeSpan(totalTicks);
		}
	}

	public TimeSpan TotalLiveRaceTime {
		get {
			long totalTicks = Laps.Where(l => l.IsFinished).Sum(t => t.LapTime.Ticks);
			if (CurrentLap == null) { return new TimeSpan(totalTicks); }
			return new TimeSpan(totalTicks + CurrentLap.LapTime.Ticks);
		}
	}
}

private struct IniTransfer {
	public string Name {get;set;}
	public string IniDef {get;set;}

	public IniTransfer(string name, string iniDef) {
		Name = name;
		IniDef = iniDef;
	}
}

private struct TrackMode {
	public string Name {get;set;}
	public bool DoRaceLights {get;set;}
	public bool ModeStarted {get;set;}
	public int MaxLaps {get;set;}
	public double MaxTime {get;set;}
	public bool WeatherEnabled {get;set;}
	public WeatherLevel InitialWeather {get;set;}
	public WeatherLevel CurrentWeather {get;set;}
	public int WeatherChanceType {get;set;}
	public int[] WeatherChanceValues {get;set;}

	public bool LightsFinished {get;set;}
	public double LightsElapsedTime {get;set;}
	public bool ModeFinished {get;set;}
	public int ElapsedLaps {get;set;}
	public double ElapsedTime {get;set;}

	public TrackMode(string name, bool doRaceLights, int maxLaps, double maxTime, bool weatherEnabled, WeatherLevel currentWeather, int weatherChanceType, int[] weatherChances) {
		Name = name;
		DoRaceLights = doRaceLights;
		MaxLaps = maxLaps;
		MaxTime = maxTime;
		WeatherEnabled = weatherEnabled;
		InitialWeather = (weatherEnabled) ? currentWeather : WeatherLevel.Clear;
		CurrentWeather = InitialWeather;
		WeatherChanceType = weatherChanceType;
		WeatherChanceValues = weatherChances;
		ModeStarted = !DoRaceLights;

		LightsFinished = false;
		LightsElapsedTime = 0;
		ModeFinished = false;
		ElapsedLaps = 0;
		ElapsedTime = 0;
	}

	public void ResetMode() {
		ModeStarted = false;
		ModeFinished = false;

		LightsFinished = false;
		LightsElapsedTime = 0;

		ElapsedLaps = 0;
		ElapsedTime = 0;
		
		CurrentWeather = InitialWeather;
	}
}

private enum WeatherLevel {
	Clear = 1,
	LightClouds,
	Cloudy,
	Overcast,
	Drizzle,
	Rain,
	HeavyRain
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

float GoToValue(float originalValue, float desiredValue, float delta)
{
    if (originalValue > desiredValue)
    {
        return (originalValue - delta <= desiredValue) ? desiredValue : originalValue - delta;
    }
    else if (originalValue < desiredValue)
    {
        return (originalValue + delta >= desiredValue) ? desiredValue : originalValue + delta;
    }
    return desiredValue;
}

bool IgnoreEntity(MyDetectedEntityInfo entity, string ignoreName) {
	return entity.IsEmpty() || entity.Name.IndexOf(ignoreName, StringComparison.InvariantCultureIgnoreCase) >= 0;
}

bool HandleErrors(ErrorMessage errorMessage) {
	bool returnBool = errorMessage.ErrorCount > 0;

	if (returnBool) {
		Echo("Check the Programmable Block display for a list of errors. Click on \"Edit Text\" to see the whole message.");
		Me.GetSurface(0).WriteText($"There are currently {errorMessage.ErrorCount} setup errors:\n\n{errorMessage.Message}");
	}

	return returnBool;
}

string BuildLine(string key, string value, int maxIdent = 999) {
	int ident = MathHelper.Clamp(DISPLAY_WIDTH - value.Length - 2, 0, maxIdent);

	return key.PadRight(ident, '.') + ": " + value;
}

string TruncateString(string input, int length)
{
    if (length < input.Length)
    {
        return input.Substring(0, length);
    }

    return input;
}

string ParseCustomData(string inputIni) {
	string returnIni = "";
	string[] lines = inputIni.Split('\n');

	for (int i = 0; i < lines.Length; i++) {
		returnIni += lines[i].Split('#')[0].Trim() + "\n";
	}
	return returnIni;
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

void SetLights(int index, bool value) {
	List<IMyLightingBlock> lights = _StartLights[index];

	foreach (IMyLightingBlock l in lights) {
		l.Enabled = true;
		l.Color = value ? Color.Red : Color.Black;
	}
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
