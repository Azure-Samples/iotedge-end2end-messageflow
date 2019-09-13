---
page_type: sample
languages:
- csharp
products:
- azure
description: "This solution is an end-to-end solution to test a full Azure IoT Edge setup. It consists of two parts:"
urlFragment: iotedge-end2end-messageflow
---

# Azure IoT Edge - End2End testing loop

This solution is an end-to-end solution to test a full Azure IoT Edge setup. It consists of two parts:
1) Two Azure Functions that run in the cloud
2) Two custom modules running on the Edge

## Features

The testing loop is started by a timer-triggered (every couple of minutes) Azure Function (Direct Method Caller), which attempts to execute a direct method on a module running in IoT Edge (Direct Method Receiver).
This module creates a new IoT messages and sends it to the Edge Hub. The message gets routed to the next module (Message Forwarder). This one forwards the message back to the Edge Hub where another route sends the message to the IoT Hub back in Azure ("$upstream").
In the cloud, a second Function is triggered by new telemetry messages on the IoT Hub. This concludes the complete loop.

All steps in the cycle are getting logged into Application Insights. This enables reporting and alerting if there are any errors at any point in the loop - and also about the end to end duration of the message from the start in the cloud until it is being received again in the cloud.

![architecture](Media/architecture_diagram.png?raw=true)

## Getting Started

### Prerequisites

- .NET Core SDK 2.2
- An Azure subscription
- An IDE, for example Visual Studio Code. Recommendation: Use VS Code with the IoT Edge extension.

## Quickstart

1) Create an Azure Function instance. Also create an Application Insights instance.
1) Create an Azure IoT Hub
    * Create a consumer group on the Event Hub-compatible endpoint called *receiverfunction*
1) Create an Edge device in the IoT Hub, name it e.g. `edgedevice1`
1) Create a container registry. This can either be a private one, for instance Azure Container Registry, or a public one such as Dockerhub.
1) Update the module.json files for both modules and replace {YOUR-CONTAINER-REPOSITORY} with your registry. E.g. `"repository": "mycr.azurecr.io/iot-edge-messageforwarder"`
1) Create a (virtual) Edge device. For this you can for example deploy [this pre-built VM](https://azuremarketplace.microsoft.com/en-us/marketplace/apps/microsoft_iot_edge.iot_edge_vm_ubuntu) from the Azure Marketplace.
1) Deploy the two modules to your Edge device (folder EdgeModules) and set the routing accordingly (see deployment.template.json for reference). Make sure to set the instrumentation key of your Application Insights instance in a new `.env` file (see sample.env).
1) Set the following settings in the Azure Function Application settings:<sup>[1](#footnote1)</sup> 
    * *iothubowner_cs*  => The IoT Hub Owner connection string of your IoT Hub
    * *iothubevents_cs* => The connection string of the built-in Event Hub compatible endpoint of your IoT Hub
    * *destinationmodules* => Comma-separates list of Edge module IDs of Direct Method Receiver modules, e.g. ("edgedevice1/DirectMethodReceiver")
1) Build and deploy the two Functions to your Azure Function app (folder CloudFunctions)

Now, the Function should trigger (default: every 2 minutes) and start the flow. After a few minutes, you should be able to see log messages in Application Insights.
If you run into any problems or have questions, feel free to open an issue here in the repo.

---
<a name="footnote1">1</a>: It's recommended to use [Azure Key Vault](https://docs.microsoft.com/en-us/azure/app-service/app-service-key-vault-references) to store your secret app settings such as connections strings
