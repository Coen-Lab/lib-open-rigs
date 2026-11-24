# Harp Behavior Interface
This module interfaces and configures a [Harp Behavior](https://github.com/harp-tech/device.behavior) device, developed by [HARP Tech](https://harp.tech/).

## Bonsai Workflow
This workflow establishes basic communication between your workflow and the device. Further functionality, such as triggering cameras, or a solenoid valve, can be found in other, standalone workflows in this, `utilities` section of this repository.

<img src="./assets/BehaviorBoard.svg" alt="workflow" width="400">

## Properties
The properties of this workflow allow the user to configure the basic device parameters and set the name of shared `Subjects` to either publish or subscribe to as an interface between your broader Bonsai workflow and a behavior board. 
## Device Features
The [behavior board](https://github.com/harp-tech/device.behavior) is a highly capable device in the Harp ecosystem with multiple features and functions.  

- **Digital outputs** (DO0-DO3) with PWM up to 10 kHz  
- **RGB LED and standard LED control** (via flick-lock and screw terminals)  
- **Valve actuation and poke detection** through RJ45 ports  
- **Servo and camera control** (2 each)  
- **Quadrature counter** (on Port 2)  
- **ADC input** (5 V analog)  
- **Clock sync input** and USB serial communication  
For full hardware specs and configuration details, see the [official repository](https://github.com/harp-tech/device.behavior).

### Input and Output `Subjects`
| **Property Name**       | **Input/Output** | **Description**                                                           |
|-------------------------|------------------|---------------------------------------------------------------------------|
| `CommandsSubjectName`   | Input            | The name of the input subject that sends commands to the device.          |
| `EventsSubjectName`     | Output           | The name of the subject that receives event messages from the device.     |
### Configuration Parameters
| **Property Name**       | **Input/Output** | **Description**                                                           |
|-------------------------|------------------|---------------------------------------------------------------------------|
| `PortName`              | Input            | Serial port used to communicate with the device (e.g., COM14).            |
| `DumpRegisters`         | Input            | Whether to dump register values from the device at startup.               |
| `Heartbeat`             | Input            | Whether to enable heartbeat messages from the device.                     |
| `ClearOnStop`           | Input            | Output lines cleared when the workflow stops, however it stops.           |
## Dependencies
### Bonsai:

In order to use this module, bonsai must have the following modules installed via the package manager or `Bonsai.config` file.
- [Harp.Behavior](https://www.nuget.org/packages/Harp.Behavior)
