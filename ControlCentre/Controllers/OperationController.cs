﻿/****************************************************************************************
*	This code originates from the software development department at					*
*	swedish insurance and private loan broker Insplanet AB.								*
*	Full license available in license.txt												*
*	This text block may not be removed or altered.                                  	*
*	The list of contributors may be extended.                                           *
*																						*
*							Mikael Axblom, head of software development, Insplanet AB	*
*																						*
*	Contributors: Mikael Axblom															*
*****************************************************************************************/
using System;
using System.Configuration;
using System.Web.Mvc;
using SystemHealthExternalInterface;
using HealthAndAuditShared;
using Microsoft.WindowsAzure.Storage.Table;

namespace ControlCentre.Controllers
{
    public class OperationController : Controller
    {
        [HttpGet]
        public ActionResult Index()
        {
            return View("docview");
        }

        [HttpGet]
        public ActionResult ViewDoc(string id)
        {
            ViewBag.documentid = id;
            SystemEvent opResult;
            if (GetDocument(id, out opResult))
            {
                return View("docview", opResult);

            }
            else
            {
                var errorInfo = new SystemEvent(SystemEvent.OperationResult.Failure);
                errorInfo.OtherInfo = "ERROR in ControlCentre itself. Could not load the requested document.";
                return View("docview",errorInfo);
            }
        }

        private bool GetDocument(string documentID, out SystemEvent systemEvent)
        {
            systemEvent = null;
            try
            {
                var storageMan = new AzureStorageManager(ConfigurationManager.AppSettings["AzureStorageConnectionString"]);
                var table = storageMan.GetTableReference(ConfigurationManager.AppSettings["TableName"]);
                var splitID = SystemEvent.DecodeIDToPartitionAndRowKey(documentID);
                var op = TableOperation.Retrieve<SystemEvent>(splitID.Item1, splitID.Item2);
                var doc = table.Execute(op);
                if (doc == null)
                {
                    return false;
                }
                systemEvent = (SystemEvent)doc.Result;
                ViewBag.pageException = HelperMethods.FormatException(systemEvent.CaughtException);
                ViewBag.objectDump = HelperMethods.GetObjectDump(systemEvent.OperationParameters);
                return true;
            }
            catch(Exception ex)
            {
                ViewBag.pageException = HelperMethods.FormatException(ex);
                return false;
            }
        }


        
    }
}