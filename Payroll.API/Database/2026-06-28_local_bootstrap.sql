-- MySQL dump 10.13  Distrib 9.6.0, for Win64 (x86_64)
--
-- Host: localhost    Database: payroll
-- ------------------------------------------------------
-- Server version	9.6.0

/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!50503 SET NAMES utf8mb4 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;

--
-- Current Database: `payroll`
--

CREATE DATABASE /*!32312 IF NOT EXISTS*/ `payroll` /*!40100 DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci */ /*!80016 DEFAULT ENCRYPTION='N' */;

USE `payroll`;

--
-- Table structure for table `attendance_settings`
--

DROP TABLE IF EXISTS `attendance_settings`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `attendance_settings` (
  `id` int NOT NULL AUTO_INCREMENT,
  `check_in_time` time NOT NULL DEFAULT '09:00:00',
  `check_out_time` time NOT NULL DEFAULT '18:00:00',
  `working_hours_calculation` varchar(80) NOT NULL DEFAULT 'First check-in and last check-out',
  `minimum_hours_for_half_day` decimal(5,2) NOT NULL DEFAULT '4.00',
  `minimum_hours_for_full_day` decimal(5,2) NOT NULL DEFAULT '8.00',
  `maximum_hours_allowed_for_full_day` decimal(5,2) NOT NULL DEFAULT '12.00',
  `allow_regularization_requests` tinyint(1) NOT NULL DEFAULT '1',
  `regularization_window` varchar(40) NOT NULL DEFAULT 'Anytime',
  `past_days_allowed` int NOT NULL DEFAULT '7',
  `restrict_regularization_requests_per_month` tinyint(1) NOT NULL DEFAULT '0',
  `max_regularization_requests_per_month` int NOT NULL DEFAULT '3',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_attendance_client` (`client_id`)
) ENGINE=InnoDB AUTO_INCREMENT=5 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `attendance_settings`
--

/*!40000 ALTER TABLE `attendance_settings` DISABLE KEYS */;
INSERT INTO `attendance_settings` VALUES (4,'09:00:00','18:00:00','First check-in and last check-out',4.00,8.00,12.00,1,'Anytime',7,0,3,'2026-06-25 22:25:32','2026-06-25 22:25:32',7);
/*!40000 ALTER TABLE `attendance_settings` ENABLE KEYS */;

--
-- Table structure for table `authpermissions`
--

DROP TABLE IF EXISTS `authpermissions`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `authpermissions` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Code` varchar(120) NOT NULL,
  `Name` varchar(150) NOT NULL,
  `Module` varchar(80) NOT NULL,
  `Description` varchar(500) DEFAULT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_AuthPermissions_Code` (`Code`)
) ENGINE=InnoDB AUTO_INCREMENT=761 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `authpermissions`
--

/*!40000 ALTER TABLE `authpermissions` DISABLE KEYS */;
INSERT INTO `authpermissions` VALUES (533,'settings.manage','Manage settings','Settings',NULL,'2026-06-25 22:18:56'),(534,'clients.manage','Manage clients','Clients',NULL,'2026-06-25 22:18:56'),(535,'employees.manage','Manage employees','Employees',NULL,'2026-06-25 22:18:56'),(536,'payroll.run','Run payroll','Payroll',NULL,'2026-06-25 22:18:56'),(537,'payroll.approve','Approve payroll','Payroll',NULL,'2026-06-25 22:18:56'),(538,'payroll.payments','Record payments','Payroll',NULL,'2026-06-25 22:18:56'),(539,'hiring.manage','Manage hiring','Hiring',NULL,'2026-06-25 22:18:56'),(540,'ess.self','Employee self service','ESS',NULL,'2026-06-25 22:18:56'),(541,'security.manage','Manage security','Security',NULL,'2026-06-25 22:18:56'),(542,'audit.view','View audit logs','Security',NULL,'2026-06-25 22:18:56'),(543,'reports.view','View and export reports','Reporting',NULL,'2026-06-25 22:18:56'),(544,'workflow.manage','Manage workflows','Workflow',NULL,'2026-06-25 22:18:56'),(545,'workflow.approve','Approve workflow tasks','Workflow',NULL,'2026-06-25 22:18:56'),(664,'tax.statutory.manage','Manage statutory income tax rules','Tax',NULL,'2026-06-27 19:35:19');
/*!40000 ALTER TABLE `authpermissions` ENABLE KEYS */;

--
-- Table structure for table `authrolepermissions`
--

DROP TABLE IF EXISTS `authrolepermissions`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `authrolepermissions` (
  `RoleId` int NOT NULL,
  `PermissionId` int NOT NULL,
  PRIMARY KEY (`RoleId`,`PermissionId`),
  KEY `FK_AuthRolePermissions_Permission` (`PermissionId`),
  CONSTRAINT `FK_AuthRolePermissions_Permission` FOREIGN KEY (`PermissionId`) REFERENCES `authpermissions` (`Id`) ON DELETE CASCADE,
  CONSTRAINT `FK_AuthRolePermissions_Role` FOREIGN KEY (`RoleId`) REFERENCES `authroles` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `authrolepermissions`
--

/*!40000 ALTER TABLE `authrolepermissions` DISABLE KEYS */;
INSERT INTO `authrolepermissions` VALUES (282,533),(282,534),(285,534),(282,535),(283,535),(285,535),(282,536),(283,536),(282,537),(284,537),(282,538),(282,539),(286,539),(282,540),(287,540),(282,541),(282,542),(284,542),(282,543),(284,543),(285,543),(282,544),(285,544),(282,545),(285,545),(282,664);
/*!40000 ALTER TABLE `authrolepermissions` ENABLE KEYS */;

--
-- Table structure for table `authroles`
--

DROP TABLE IF EXISTS `authroles`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `authroles` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Code` varchar(80) NOT NULL,
  `Name` varchar(150) NOT NULL,
  `Description` varchar(500) DEFAULT NULL,
  `IsSystem` tinyint(1) NOT NULL DEFAULT '0',
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_AuthRoles_Code` (`Code`)
) ENGINE=InnoDB AUTO_INCREMENT=359 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `authroles`
--

/*!40000 ALTER TABLE `authroles` DISABLE KEYS */;
INSERT INTO `authroles` VALUES (282,'super_admin','Super Admin','Full platform access',1,'2026-06-25 22:18:56'),(283,'payroll_maker','Payroll Maker','Can prepare and maintain payroll drafts',1,'2026-06-25 22:18:56'),(284,'payroll_approver','Payroll Approver','Can approve payroll and review audit evidence',1,'2026-06-25 22:18:56'),(285,'hr_manager','HR Manager','Can manage employees and HR setup',1,'2026-06-25 22:18:56'),(286,'hiring_manager','Hiring Manager','Can manage hiring workflows',1,'2026-06-25 22:18:56'),(287,'employee','Employee','Employee self-service access',1,'2026-06-25 22:18:56');
/*!40000 ALTER TABLE `authroles` ENABLE KEYS */;

--
-- Table structure for table `authuserroles`
--

DROP TABLE IF EXISTS `authuserroles`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `authuserroles` (
  `UserId` int NOT NULL,
  `RoleId` int NOT NULL,
  PRIMARY KEY (`UserId`,`RoleId`),
  KEY `FK_AuthUserRoles_Role` (`RoleId`),
  CONSTRAINT `FK_AuthUserRoles_Role` FOREIGN KEY (`RoleId`) REFERENCES `authroles` (`Id`) ON DELETE CASCADE,
  CONSTRAINT `FK_AuthUserRoles_User` FOREIGN KEY (`UserId`) REFERENCES `authusers` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `authuserroles`
--

/*!40000 ALTER TABLE `authuserroles` DISABLE KEYS */;
INSERT INTO `authuserroles` VALUES (3,282);
/*!40000 ALTER TABLE `authuserroles` ENABLE KEYS */;

--
-- Table structure for table `authusers`
--

DROP TABLE IF EXISTS `authusers`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `authusers` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Email` varchar(190) NOT NULL,
  `DisplayName` varchar(190) NOT NULL,
  `PasswordHash` varchar(500) NOT NULL,
  `ClientId` int DEFAULT NULL,
  `IsActive` tinyint(1) NOT NULL DEFAULT '1',
  `MustChangePassword` tinyint(1) NOT NULL DEFAULT '0',
  `LastLoginAt` datetime DEFAULT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `EmployeeId` int DEFAULT NULL,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_AuthUsers_Email` (`Email`)
) ENGINE=InnoDB AUTO_INCREMENT=4 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `authusers`
--

/*!40000 ALTER TABLE `authusers` DISABLE KEYS */;
INSERT INTO `authusers` VALUES (3,'admin@paymint.local','System Administrator','PBKDF2-SHA256$120000$x1mtVZvgZ2GmSFCTfIbazw==$goQG20WdpH1h8MT8obWh2KugYaI7r5ihM2P3BQMQEwA=',NULL,1,1,'2026-06-27 22:32:30','2026-06-25 22:18:56','2026-06-28 04:02:30',NULL);
/*!40000 ALTER TABLE `authusers` ENABLE KEYS */;

--
-- Table structure for table `clientpayschedules`
--

DROP TABLE IF EXISTS `clientpayschedules`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `clientpayschedules` (
  `ClientId` int NOT NULL,
  `WorkWeek` varchar(80) NOT NULL DEFAULT 'Monday - Friday',
  `SalaryDays` varchar(80) NOT NULL DEFAULT 'Actual days',
  `FixedDays` varchar(10) NOT NULL DEFAULT '30',
  `PayDay` varchar(80) NOT NULL DEFAULT 'Last working day',
  `FirstPayPeriod` varchar(7) NOT NULL DEFAULT '',
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`ClientId`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `clientpayschedules`
--

/*!40000 ALTER TABLE `clientpayschedules` DISABLE KEYS */;
INSERT INTO `clientpayschedules` VALUES (10,'Monday - Friday','Actual days','30','Last working day','2026-05','2026-06-28 09:23:38');
/*!40000 ALTER TABLE `clientpayschedules` ENABLE KEYS */;

--
-- Table structure for table `clients`
--

DROP TABLE IF EXISTS `clients`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `clients` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Name` varchar(250) NOT NULL,
  `Code` varchar(50) DEFAULT NULL,
  `ContactPerson` varchar(150) DEFAULT NULL,
  `Email` varchar(150) DEFAULT NULL,
  `Phone` varchar(50) DEFAULT NULL,
  `Address` varchar(500) DEFAULT NULL,
  `IsActive` tinyint(1) NOT NULL DEFAULT '1',
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `PayScheduleJson` json DEFAULT NULL,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=11 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `clients`
--

/*!40000 ALTER TABLE `clients` DISABLE KEYS */;
INSERT INTO `clients` VALUES (7,'Acme Technologies','ACME','Riya Sharma','hr@acme.test','9876543210','Bengaluru',1,'2026-06-25 22:18:55','2026-06-25 22:18:55',NULL),(8,'Northwind Services','NORTH','Arjun Mehta','ops@northwind.test','9123456780','Pune',1,'2026-06-25 22:18:55','2026-06-25 22:18:55',NULL),(10,'RECL','RECL','GA Digital','','','RECL pan India payroll client',1,'2026-06-28 07:45:26','2026-06-28 07:45:26','{\"payDay\": \"Last working day\", \"source\": \"RECL DATA.xlsx\", \"workWeek\": \"Monday - Friday\", \"fixedDays\": \"30\", \"salaryDays\": \"Actual days\", \"firstPayPeriod\": \"2026-05\"}');
/*!40000 ALTER TABLE `clients` ENABLE KEYS */;

--
-- Table structure for table `company_income_tax_section_settings`
--

DROP TABLE IF EXISTS `company_income_tax_section_settings`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `company_income_tax_section_settings` (
  `id` int NOT NULL AUTO_INCREMENT,
  `company_id` int NOT NULL,
  `financial_year_id` int DEFAULT NULL,
  `financial_year` varchar(10) NOT NULL,
  `tax_deduction_section_id` int NOT NULL,
  `is_enabled` tinyint(1) NOT NULL DEFAULT '1',
  `is_proof_required` tinyint(1) NOT NULL DEFAULT '1',
  `is_approval_required` tinyint(1) NOT NULL DEFAULT '1',
  `display_order` int NOT NULL DEFAULT '100',
  `created_by` int DEFAULT NULL,
  `created_on` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `modified_by` int DEFAULT NULL,
  `modified_on` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_company_tax_section` (`company_id`,`financial_year`,`tax_deduction_section_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `company_income_tax_section_settings`
--

/*!40000 ALTER TABLE `company_income_tax_section_settings` DISABLE KEYS */;
/*!40000 ALTER TABLE `company_income_tax_section_settings` ENABLE KEYS */;

--
-- Table structure for table `departmentheadassignments`
--

DROP TABLE IF EXISTS `departmentheadassignments`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `departmentheadassignments` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ClientId` int NOT NULL,
  `Department` varchar(100) NOT NULL,
  `UserId` int NOT NULL,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_DepartmentHeadAssignment` (`ClientId`,`Department`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `departmentheadassignments`
--

/*!40000 ALTER TABLE `departmentheadassignments` DISABLE KEYS */;
/*!40000 ALTER TABLE `departmentheadassignments` ENABLE KEYS */;

--
-- Table structure for table `dropdownmasters`
--

DROP TABLE IF EXISTS `dropdownmasters`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `dropdownmasters` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Type` varchar(100) NOT NULL,
  `Value` varchar(200) NOT NULL,
  `IsActive` tinyint(1) NOT NULL DEFAULT '1',
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_DropdownMasters_Type_Value` (`Type`,`Value`)
) ENGINE=InnoDB AUTO_INCREMENT=128 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `dropdownmasters`
--

/*!40000 ALTER TABLE `dropdownmasters` DISABLE KEYS */;
INSERT INTO `dropdownmasters` VALUES (33,'Department','Engineering',1,'2026-06-25 22:18:55','2026-06-25 22:18:55'),(34,'Department','Finance',1,'2026-06-25 22:18:55','2026-06-25 22:18:55'),(35,'Department','HR',1,'2026-06-25 22:18:55','2026-06-25 22:18:55'),(36,'Designation','Software Engineer',1,'2026-06-25 22:18:55','2026-06-25 22:18:55'),(37,'Designation','Payroll Executive',1,'2026-06-25 22:18:55','2026-06-25 22:18:55'),(38,'Designation','Manager',1,'2026-06-25 22:18:55','2026-06-25 22:18:55'),(39,'Employment Type','Full Time',1,'2026-06-25 22:18:55','2026-06-25 22:18:55'),(40,'Employee Grade','L1',1,'2026-06-25 22:18:55','2026-06-25 22:18:55'),(41,'Employee Grade','L2',1,'2026-06-25 22:18:55','2026-06-25 22:18:55'),(42,'Cost Center','CC-TECH',1,'2026-06-25 22:18:55','2026-06-25 22:18:55'),(43,'Designation','Executive Assistant',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(44,'Designation','Home Office, MoP',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(45,'Designation','HR',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(46,'Designation','IT/Data Consultant',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(47,'Designation','Junior Accountant',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(48,'Designation','MTS',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(49,'Designation','Office Attendant',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(50,'Designation','Operations Assistant',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(51,'Designation','Peon',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(52,'Designation','Project Associate(Fin.)',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(53,'Designation','Project Engineer',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(54,'Designation','Project Engineer IT',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(55,'State','Karnataka',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(56,'District:Karnataka','Bengaluru Urban',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(57,'State','Himachal Pradesh',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(58,'District:Himachal Pradesh','Solan',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(59,'State','Madhya Pradesh',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(60,'District:Madhya Pradesh','Bhopal',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(61,'State','Tamil Nadu',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(62,'District:Tamil Nadu','Chennai',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(63,'State','Haryana',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(64,'District:Haryana','Gurugram',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(65,'State','Assam',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(66,'District:Assam','Kamrup Metropolitan',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(67,'State','Rajasthan',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(68,'District:Rajasthan','Jaipur',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(69,'State','Jammu & Kashmir',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(70,'District:Jammu & Kashmir','Jammu',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(71,'State','West Bengal',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(72,'District:West Bengal','Kolkata',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(73,'State','Uttar Pradesh',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(74,'District:Uttar Pradesh','Lucknow',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(75,'State','Delhi',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(76,'District:Delhi','New Delhi',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(77,'State','Maharashtra',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(78,'District:Maharashtra','Mumbai',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(80,'District:Haryana','Panchkula',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(81,'State','Bihar',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(82,'District:Bihar','Patna',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(83,'State','Chhattisgarh',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(84,'District:Chhattisgarh','Raipur',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(85,'State','Jharkhand',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(86,'District:Jharkhand','Ranchi',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(90,'District:Himachal Pradesh','Shimla',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(91,'State','Kerala',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(92,'District:Kerala','Thiruvananthapuram',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(93,'State','Gujarat',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(94,'District:Gujarat','Vadodara',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(95,'State','Andhra Pradesh',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(96,'District:Andhra Pradesh','NTR',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(97,'City:Karnataka','Bengaluru',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(98,'City:Maharashtra','Pune',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(99,'City:Himachal Pradesh','Baddi-Barotiwala-Nalagarh',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(100,'City:Madhya Pradesh','Bhopal',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(101,'City:Tamil Nadu','Chennai',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(102,'City:Haryana','Gurugram',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(103,'City:Assam','Guwahati',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(104,'City:Rajasthan','Jaipur',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(105,'City:Jammu & Kashmir','Jammu',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(106,'City:West Bengal','Kolkata',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(107,'City:Uttar Pradesh','Lucknow',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(108,'City:Delhi','New Delhi',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(109,'City:Maharashtra','Mumbai',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(110,'City:Haryana','Panchkula',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(111,'City:Bihar','Patna',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(112,'City:Chhattisgarh','Raipur',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(113,'City:Jharkhand','Ranchi',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(114,'City:Himachal Pradesh','Shimla',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(115,'City:Kerala','Thiruvananthapuram',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(116,'City:Gujarat','Vadodara',1,'2026-06-28 08:26:00','2026-06-28 08:26:00'),(117,'City:Andhra Pradesh','Vijayawada',1,'2026-06-28 08:26:00','2026-06-28 08:26:00');
/*!40000 ALTER TABLE `dropdownmasters` ENABLE KEYS */;

--
-- Table structure for table `employee_daily_attendance`
--

DROP TABLE IF EXISTS `employee_daily_attendance`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `employee_daily_attendance` (
  `id` int NOT NULL AUTO_INCREMENT,
  `client_id` int NOT NULL,
  `employee_id` int NOT NULL,
  `attendance_date` date NOT NULL,
  `status` varchar(30) NOT NULL DEFAULT 'Present',
  `payable_value` decimal(4,2) NOT NULL DEFAULT '1.00',
  `remarks` varchar(600) DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_daily_attendance_employee_date` (`client_id`,`employee_id`,`attendance_date`),
  KEY `IX_daily_attendance_client_date` (`client_id`,`attendance_date`),
  KEY `FK_daily_attendance_employee` (`employee_id`),
  CONSTRAINT `FK_daily_attendance_employee` FOREIGN KEY (`employee_id`) REFERENCES `employees` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=151 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `employee_daily_attendance`
--

/*!40000 ALTER TABLE `employee_daily_attendance` DISABLE KEYS */;
INSERT INTO `employee_daily_attendance` VALUES (1,7,595,'2026-06-01','LOP',0.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(2,7,595,'2026-06-02','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(3,7,595,'2026-06-03','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(4,7,595,'2026-06-04','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(5,7,595,'2026-06-05','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(6,7,595,'2026-06-06','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(7,7,595,'2026-06-07','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(8,7,595,'2026-06-08','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(9,7,595,'2026-06-09','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(10,7,595,'2026-06-10','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(11,7,595,'2026-06-11','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(12,7,595,'2026-06-12','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(13,7,595,'2026-06-13','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(14,7,595,'2026-06-14','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(15,7,595,'2026-06-15','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(16,7,595,'2026-06-16','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(17,7,595,'2026-06-17','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(18,7,595,'2026-06-18','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(19,7,595,'2026-06-19','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(20,7,595,'2026-06-20','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(21,7,595,'2026-06-21','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(22,7,595,'2026-06-22','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(23,7,595,'2026-06-23','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(24,7,595,'2026-06-24','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(25,7,595,'2026-06-25','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(26,7,595,'2026-06-26','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(27,7,595,'2026-06-27','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(28,7,595,'2026-06-28','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(29,7,595,'2026-06-29','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15'),(30,7,595,'2026-06-30','Present',1.00,'Filled during payroll attendance review','2026-06-26 17:28:15','2026-06-26 17:28:15');
/*!40000 ALTER TABLE `employee_daily_attendance` ENABLE KEYS */;

--
-- Table structure for table `employee_leave_balances`
--

DROP TABLE IF EXISTS `employee_leave_balances`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `employee_leave_balances` (
  `id` int NOT NULL AUTO_INCREMENT,
  `employee_id` int NOT NULL,
  `leave_type_id` int NOT NULL,
  `balance_date` date NOT NULL,
  `balance_count` decimal(10,2) NOT NULL DEFAULT '0.00',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  `BalanceCount` decimal(10,2) GENERATED ALWAYS AS (`balance_count`) VIRTUAL,
  `BalanceDate` date GENERATED ALWAYS AS (`balance_date`) VIRTUAL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_employee_leave_balances_employee_type_date` (`employee_id`,`leave_type_id`,`balance_date`),
  KEY `IX_employee_leave_balances_employee` (`employee_id`),
  KEY `FK_employee_leave_balances_leave_type` (`leave_type_id`),
  CONSTRAINT `FK_employee_leave_balances_employee` FOREIGN KEY (`employee_id`) REFERENCES `employees` (`Id`) ON DELETE CASCADE,
  CONSTRAINT `FK_employee_leave_balances_leave_type` FOREIGN KEY (`leave_type_id`) REFERENCES `leave_types` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=373 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `employee_leave_balances`
--

/*!40000 ALTER TABLE `employee_leave_balances` DISABLE KEYS */;
/*!40000 ALTER TABLE `employee_leave_balances` ENABLE KEYS */;

--
-- Table structure for table `employee_monthly_attendance`
--

DROP TABLE IF EXISTS `employee_monthly_attendance`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `employee_monthly_attendance` (
  `id` int NOT NULL AUTO_INCREMENT,
  `client_id` int NOT NULL,
  `employee_id` int NOT NULL,
  `attendance_month` varchar(7) NOT NULL,
  `working_days` decimal(5,2) NOT NULL DEFAULT '0.00',
  `present_days` decimal(5,2) NOT NULL DEFAULT '0.00',
  `payable_days` decimal(5,2) NOT NULL DEFAULT '0.00',
  `lop_days` decimal(5,2) NOT NULL DEFAULT '0.00',
  `source_type` varchar(30) NOT NULL DEFAULT 'Monthly',
  `remarks` varchar(600) DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_monthly_attendance_employee_month` (`client_id`,`employee_id`,`attendance_month`),
  KEY `IX_monthly_attendance_client_month` (`client_id`,`attendance_month`),
  KEY `FK_monthly_attendance_employee` (`employee_id`),
  CONSTRAINT `FK_monthly_attendance_employee` FOREIGN KEY (`employee_id`) REFERENCES `employees` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=111 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `employee_monthly_attendance`
--

/*!40000 ALTER TABLE `employee_monthly_attendance` DISABLE KEYS */;
INSERT INTO `employee_monthly_attendance` VALUES (11,7,595,'2026-06',30.00,29.00,29.00,1.00,'Date-wise','Rolled up from date-wise attendance','2026-06-26 17:28:15','2026-06-26 17:28:15'),(16,10,709,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 3','2026-06-28 07:45:27','2026-06-28 07:45:27'),(17,10,710,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 4','2026-06-28 07:45:27','2026-06-28 07:45:27'),(18,10,711,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 5','2026-06-28 07:45:27','2026-06-28 07:45:27'),(19,10,712,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 6','2026-06-28 07:45:27','2026-06-28 07:45:27'),(20,10,713,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 50','2026-06-28 07:45:27','2026-06-28 07:45:27'),(21,10,714,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 8','2026-06-28 07:45:27','2026-06-28 07:45:27'),(22,10,715,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 9','2026-06-28 07:45:27','2026-06-28 07:45:27'),(23,10,716,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 10','2026-06-28 07:45:27','2026-06-28 07:45:27'),(24,10,717,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 11','2026-06-28 07:45:27','2026-06-28 07:45:27'),(25,10,718,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 12','2026-06-28 07:45:27','2026-06-28 07:45:27'),(26,10,719,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 13','2026-06-28 07:45:27','2026-06-28 07:45:27'),(27,10,720,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 14','2026-06-28 07:45:27','2026-06-28 07:45:27'),(28,10,721,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 15','2026-06-28 07:45:27','2026-06-28 07:45:27'),(29,10,722,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 16','2026-06-28 07:45:27','2026-06-28 07:45:27'),(30,10,723,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 17','2026-06-28 07:45:27','2026-06-28 07:45:27'),(31,10,724,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 18','2026-06-28 07:45:27','2026-06-28 07:45:27'),(32,10,725,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 19','2026-06-28 07:45:27','2026-06-28 07:45:27'),(33,10,726,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 20','2026-06-28 07:45:27','2026-06-28 07:45:27'),(34,10,727,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 21','2026-06-28 07:45:27','2026-06-28 07:45:27'),(35,10,728,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 22','2026-06-28 07:45:27','2026-06-28 07:45:27'),(36,10,729,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 23','2026-06-28 07:45:27','2026-06-28 07:45:27'),(37,10,730,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 24','2026-06-28 07:45:27','2026-06-28 07:45:27'),(38,10,731,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 25','2026-06-28 07:45:27','2026-06-28 07:45:27'),(39,10,732,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 26','2026-06-28 07:45:27','2026-06-28 07:45:27'),(40,10,733,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 27','2026-06-28 07:45:27','2026-06-28 07:45:27'),(41,10,734,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 28','2026-06-28 07:45:27','2026-06-28 07:45:27'),(42,10,735,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 29','2026-06-28 07:45:27','2026-06-28 07:45:27'),(43,10,736,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 30','2026-06-28 07:45:27','2026-06-28 07:45:27'),(44,10,737,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 31','2026-06-28 07:45:27','2026-06-28 07:45:27'),(45,10,738,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 32','2026-06-28 07:45:27','2026-06-28 07:45:27'),(46,10,739,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 33','2026-06-28 07:45:27','2026-06-28 07:45:27'),(47,10,740,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 34','2026-06-28 07:45:27','2026-06-28 07:45:27'),(48,10,741,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 35','2026-06-28 07:45:27','2026-06-28 07:45:27'),(49,10,742,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 36','2026-06-28 07:45:27','2026-06-28 07:45:27'),(50,10,743,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 37','2026-06-28 07:45:27','2026-06-28 07:45:27'),(51,10,744,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 38','2026-06-28 07:45:27','2026-06-28 07:45:27'),(52,10,745,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 39','2026-06-28 07:45:27','2026-06-28 07:45:27'),(53,10,746,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 40','2026-06-28 07:45:27','2026-06-28 07:45:27'),(54,10,747,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 41','2026-06-28 07:45:27','2026-06-28 07:45:27'),(55,10,748,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 42','2026-06-28 07:45:27','2026-06-28 07:45:27'),(56,10,749,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 43','2026-06-28 07:45:27','2026-06-28 07:45:27'),(57,10,750,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 44','2026-06-28 07:45:27','2026-06-28 07:45:27'),(58,10,751,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 45','2026-06-28 07:45:27','2026-06-28 07:45:27'),(59,10,752,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 46','2026-06-28 07:45:27','2026-06-28 07:45:27'),(60,10,753,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 47','2026-06-28 07:45:27','2026-06-28 07:45:27'),(61,10,754,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 48','2026-06-28 07:45:27','2026-06-28 07:45:27'),(62,10,755,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 49','2026-06-28 07:45:27','2026-06-28 07:45:27'),(64,10,757,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 51','2026-06-28 07:45:27','2026-06-28 07:45:27'),(65,10,758,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 52','2026-06-28 07:45:27','2026-06-28 07:45:27'),(66,10,759,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 53','2026-06-28 07:45:27','2026-06-28 07:45:27'),(67,10,760,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 54','2026-06-28 07:45:27','2026-06-28 07:45:27'),(68,10,761,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 55','2026-06-28 07:45:27','2026-06-28 07:45:27'),(69,10,762,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 56','2026-06-28 07:45:27','2026-06-28 07:45:27'),(70,10,763,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 57','2026-06-28 07:45:27','2026-06-28 07:45:27'),(71,10,764,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 58','2026-06-28 07:45:27','2026-06-28 07:45:27'),(72,10,765,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 59','2026-06-28 07:45:27','2026-06-28 07:45:27'),(73,10,766,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 60','2026-06-28 07:45:27','2026-06-28 07:45:27'),(74,10,767,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 61','2026-06-28 07:45:27','2026-06-28 07:45:27'),(75,10,768,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 62','2026-06-28 07:45:27','2026-06-28 07:45:27'),(76,10,769,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 63','2026-06-28 07:45:27','2026-06-28 07:45:27'),(77,10,770,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 64','2026-06-28 07:45:27','2026-06-28 07:45:27'),(78,10,771,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 65','2026-06-28 07:45:27','2026-06-28 07:45:27'),(79,10,772,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 66','2026-06-28 07:45:27','2026-06-28 07:45:27'),(80,10,773,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 67','2026-06-28 07:45:27','2026-06-28 07:45:27'),(81,10,774,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 68','2026-06-28 07:45:27','2026-06-28 07:45:27'),(82,10,775,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 69','2026-06-28 07:45:27','2026-06-28 07:45:27'),(83,10,776,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 70','2026-06-28 07:45:27','2026-06-28 07:45:27'),(84,10,777,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 71','2026-06-28 07:45:27','2026-06-28 07:45:27'),(85,10,778,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 72','2026-06-28 07:45:27','2026-06-28 07:45:27'),(86,10,779,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 73','2026-06-28 07:45:27','2026-06-28 07:45:27'),(87,10,780,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 74','2026-06-28 07:45:27','2026-06-28 07:45:27'),(88,10,781,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 75','2026-06-28 07:45:27','2026-06-28 07:45:27'),(89,10,782,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 76','2026-06-28 07:45:27','2026-06-28 07:45:27'),(90,10,783,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 77','2026-06-28 07:45:27','2026-06-28 07:45:27'),(91,10,784,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 78','2026-06-28 07:45:27','2026-06-28 07:45:27'),(92,10,785,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 79','2026-06-28 07:45:27','2026-06-28 07:45:27'),(93,10,786,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 80','2026-06-28 07:45:27','2026-06-28 07:45:27'),(94,10,787,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 81','2026-06-28 07:45:27','2026-06-28 07:45:27'),(95,10,788,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 82','2026-06-28 07:45:27','2026-06-28 07:45:27'),(96,10,789,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 83','2026-06-28 07:45:27','2026-06-28 07:45:27'),(97,10,790,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 84','2026-06-28 07:45:27','2026-06-28 07:45:27'),(98,10,791,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 85','2026-06-28 07:45:27','2026-06-28 07:45:27'),(99,10,792,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 86','2026-06-28 07:45:27','2026-06-28 07:45:27'),(100,10,793,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 87','2026-06-28 07:45:27','2026-06-28 07:45:27'),(101,10,794,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 88','2026-06-28 07:45:27','2026-06-28 07:45:27'),(102,10,795,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 89','2026-06-28 07:45:27','2026-06-28 07:45:27'),(103,10,796,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 90','2026-06-28 07:45:27','2026-06-28 07:45:27'),(104,10,797,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 91','2026-06-28 07:45:27','2026-06-28 07:45:27'),(105,10,798,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 92','2026-06-28 07:45:27','2026-06-28 07:45:27'),(106,10,799,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 93','2026-06-28 07:45:27','2026-06-28 07:45:27'),(107,10,800,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 94','2026-06-28 07:45:27','2026-06-28 07:45:27'),(108,10,801,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 95','2026-06-28 07:45:27','2026-06-28 07:45:27'),(109,10,802,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 96','2026-06-28 07:45:27','2026-06-28 07:45:27'),(110,10,803,'2026-05',31.00,31.00,31.00,0.00,'Excel','RECL DATA.xlsx seed row 7 duplicate code resolved','2026-06-28 07:50:43','2026-06-28 07:50:43');
/*!40000 ALTER TABLE `employee_monthly_attendance` ENABLE KEYS */;

--
-- Table structure for table `employee_tax_declaration_headers`
--

DROP TABLE IF EXISTS `employee_tax_declaration_headers`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `employee_tax_declaration_headers` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `employee_id` int NOT NULL,
  `client_id` int NOT NULL,
  `financial_year` varchar(10) NOT NULL,
  `financial_year_id` int DEFAULT NULL,
  `activity_code` varchar(40) NOT NULL,
  `status` varchar(30) NOT NULL DEFAULT 'Draft',
  `submitted_at` datetime DEFAULT NULL,
  `approved_by_user_id` int DEFAULT NULL,
  `approved_at` datetime DEFAULT NULL,
  `remarks` varchar(1000) DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_employee_tax_declaration_header` (`employee_id`,`financial_year`,`activity_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `employee_tax_declaration_headers`
--

/*!40000 ALTER TABLE `employee_tax_declaration_headers` DISABLE KEYS */;
/*!40000 ALTER TABLE `employee_tax_declaration_headers` ENABLE KEYS */;

--
-- Table structure for table `employee_tax_declaration_lines`
--

DROP TABLE IF EXISTS `employee_tax_declaration_lines`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `employee_tax_declaration_lines` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `header_id` bigint NOT NULL,
  `section_id` int NOT NULL,
  `amount` decimal(14,2) NOT NULL DEFAULT '0.00',
  `approved_amount` decimal(14,2) DEFAULT NULL,
  `status` varchar(30) NOT NULL DEFAULT 'Draft',
  `remarks` varchar(1000) DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_employee_tax_declaration_line` (`header_id`,`section_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `employee_tax_declaration_lines`
--

/*!40000 ALTER TABLE `employee_tax_declaration_lines` DISABLE KEYS */;
/*!40000 ALTER TABLE `employee_tax_declaration_lines` ENABLE KEYS */;

--
-- Table structure for table `employee_tax_declaration_proofs`
--

DROP TABLE IF EXISTS `employee_tax_declaration_proofs`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `employee_tax_declaration_proofs` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `declaration_id` bigint NOT NULL,
  `file_name` varchar(260) NOT NULL,
  `content_type` varchar(100) DEFAULT NULL,
  `file_path` varchar(500) NOT NULL,
  `uploaded_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `employee_tax_declaration_proofs`
--

/*!40000 ALTER TABLE `employee_tax_declaration_proofs` DISABLE KEYS */;
/*!40000 ALTER TABLE `employee_tax_declaration_proofs` ENABLE KEYS */;

--
-- Table structure for table `employee_tax_declarations`
--

DROP TABLE IF EXISTS `employee_tax_declarations`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `employee_tax_declarations` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `employee_id` int NOT NULL,
  `client_id` int NOT NULL,
  `financial_year` varchar(10) NOT NULL,
  `financial_year_id` int DEFAULT NULL,
  `section_id` int NOT NULL,
  `declared_amount` decimal(14,2) NOT NULL DEFAULT '0.00',
  `approved_amount` decimal(14,2) DEFAULT NULL,
  `status` varchar(30) NOT NULL DEFAULT 'Draft',
  `planned_amount` decimal(14,2) NOT NULL DEFAULT '0.00',
  `actual_amount` decimal(14,2) NOT NULL DEFAULT '0.00',
  `remarks` varchar(1000) DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_employee_tax_declaration_section` (`employee_id`,`financial_year`,`section_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `employee_tax_declarations`
--

/*!40000 ALTER TABLE `employee_tax_declarations` DISABLE KEYS */;
/*!40000 ALTER TABLE `employee_tax_declarations` ENABLE KEYS */;

--
-- Table structure for table `employee_tax_projection_runs`
--

DROP TABLE IF EXISTS `employee_tax_projection_runs`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `employee_tax_projection_runs` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `employee_id` int NOT NULL,
  `client_id` int NOT NULL,
  `financial_year` varchar(10) NOT NULL,
  `pay_period` varchar(7) NOT NULL,
  `regime` varchar(10) NOT NULL,
  `taxable_income` decimal(14,2) NOT NULL DEFAULT '0.00',
  `approved_deductions` decimal(14,2) NOT NULL DEFAULT '0.00',
  `annual_tax` decimal(14,2) NOT NULL DEFAULT '0.00',
  `monthly_tds` decimal(14,2) NOT NULL DEFAULT '0.00',
  `calculation_json` json DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `employee_tax_projection_runs`
--

/*!40000 ALTER TABLE `employee_tax_projection_runs` DISABLE KEYS */;
/*!40000 ALTER TABLE `employee_tax_projection_runs` ENABLE KEYS */;

--
-- Table structure for table `employee_tax_regime_selections`
--

DROP TABLE IF EXISTS `employee_tax_regime_selections`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `employee_tax_regime_selections` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `employee_id` int NOT NULL,
  `client_id` int NOT NULL,
  `financial_year` varchar(10) NOT NULL,
  `financial_year_id` int DEFAULT NULL,
  `regime` varchar(10) NOT NULL,
  `status` varchar(30) NOT NULL DEFAULT 'Submitted',
  `submitted_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `approved_by_user_id` int DEFAULT NULL,
  `approved_at` datetime DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_employee_tax_regime` (`employee_id`,`financial_year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `employee_tax_regime_selections`
--

/*!40000 ALTER TABLE `employee_tax_regime_selections` DISABLE KEYS */;
/*!40000 ALTER TABLE `employee_tax_regime_selections` ENABLE KEYS */;

--
-- Table structure for table `employeepaymentdetails`
--

DROP TABLE IF EXISTS `employeepaymentdetails`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `employeepaymentdetails` (
  `EmployeeId` int NOT NULL,
  `BankAccountNo` varchar(100) NOT NULL DEFAULT '',
  `IfscCode` varchar(40) NOT NULL DEFAULT '',
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`EmployeeId`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `employeepaymentdetails`
--

/*!40000 ALTER TABLE `employeepaymentdetails` DISABLE KEYS */;
INSERT INTO `employeepaymentdetails` VALUES (595,'','','2026-06-28 09:23:38'),(596,'','','2026-06-28 09:23:38'),(597,'','','2026-06-28 09:23:38'),(598,'','','2026-06-28 09:23:38'),(599,'','','2026-06-28 09:23:38'),(600,'','','2026-06-28 09:23:38'),(601,'','','2026-06-28 09:23:38'),(602,'','','2026-06-28 09:23:38'),(603,'','','2026-06-28 09:23:38'),(604,'','','2026-06-28 09:23:38'),(615,'','','2026-06-28 09:23:38'),(616,'','','2026-06-28 09:23:38'),(676,'','','2026-06-28 09:23:38'),(709,'33722161464','SBIN0005198','2026-06-28 09:23:38'),(710,'45150100004044','BARB0BELTHA','2026-06-28 09:23:38'),(711,'0457120000041','CNRB0000457','2026-06-28 09:23:38'),(712,'38991250870','SBIN0040702','2026-06-28 09:23:38'),(713,'50100156871214','HDFC0009088','2026-06-28 09:23:39'),(714,'7218768624','IDIB000G544','2026-06-28 09:23:39'),(715,'923010050649438','UTIB0000512','2026-06-28 09:23:39'),(716,'33100100025019','BARB0VILLUP','2026-06-28 09:23:39'),(717,'20120947998','SBIN0003030','2026-06-28 09:23:39'),(718,'40573917888','SBIN0031595','2026-06-28 09:23:39'),(719,'660318210009719','BKID0006603','2026-06-28 09:23:39'),(720,'20239611421','SBIN0008547','2026-06-28 09:23:39'),(721,'50100436294491','HDFC0002758','2026-06-28 09:23:39'),(722,'040701516329','ICIC0000407','2026-06-28 09:23:39'),(723,'20147235426','SBIN0011857','2026-06-28 09:23:39'),(724,'20248664620','SBIN0007679','2026-06-28 09:23:39'),(725,'37895364374','SBIN0009125','2026-06-28 09:23:39'),(726,'20397956804','SBIN0012361','2026-06-28 09:23:40'),(727,'32933760758','SBIN0011213','2026-06-28 09:23:40'),(728,'50100275440494','HDFC0000722','2026-06-28 09:23:40'),(729,'520101265052317','UBIN0901067','2026-06-28 09:23:40'),(730,'35225519635','SBIN0001712','2026-06-28 09:23:40'),(731,'32236412272','SBIN0012302','2026-06-28 09:23:40'),(732,'20016364994','SBIN0001435','2026-06-28 09:23:40'),(733,'7577000100052549','PUNB0757700','2026-06-28 09:23:40'),(734,'38237856835','SBIN0017109','2026-06-28 09:23:40'),(735,'37864781555','SBIN0060301','2026-06-28 09:23:40'),(736,'68011261658','MAHB0000439','2026-06-28 09:23:40'),(737,'68011168274','MAHB0001580','2026-06-28 09:23:40'),(738,'101008353860','UTIB0SJSD01','2026-06-28 09:23:40'),(739,'50100143405779','HDFC0000152','2026-06-28 09:23:41'),(740,'20303018563','SBIN0030172','2026-06-28 09:23:41'),(741,'50100755706209','HDFC0003416','2026-06-28 09:23:41'),(742,'32554438435','SBIN0012147','2026-06-28 09:23:41'),(743,'35726673257','SBIN0009750','2026-06-28 09:23:41'),(744,'499710110009959','BKID0004997','2026-06-28 09:23:41'),(745,'20440725175','SBIN0014827','2026-06-28 09:23:41'),(746,'39218148932','SBIN0050118','2026-06-28 09:23:41'),(747,'98360100005160','BARB0EXTSIM','2026-06-28 09:23:41'),(748,'1824101014068','CNRB0001824','2026-06-28 09:23:41'),(749,'67228205786','SBIN0070667','2026-06-28 09:23:41'),(750,'41452340606','SBIN0070424','2026-06-28 09:23:42'),(751,'32929565710','SBIN0010787','2026-06-28 09:23:42'),(752,'67228271680','SBIN0070422','2026-06-28 09:23:42'),(753,'67055368643','SBIN0070018','2026-06-28 09:23:42'),(754,'31792992207','SBIN0000212','2026-06-28 09:23:42'),(755,'656801500528','ICIC0001438','2026-06-28 09:23:42'),(757,'20212440078','SBIN0016959','2026-06-28 09:23:42'),(758,'50100572884244','HDFC0009062','2026-06-28 09:23:42'),(759,'50276068075','IDIB000M720','2026-06-28 09:23:42'),(760,'50100269185958','HDFC0000479','2026-06-28 09:23:42'),(761,'37946763770','SBIN0002506','2026-06-28 09:23:42'),(762,'42049213331','SBIN0008110','2026-06-28 09:23:43'),(763,'20415803204','SBIN0011469','2026-06-28 09:23:43'),(764,'50100263928177','HDFC0000278','2026-06-28 09:23:43'),(765,'1967000102620271','PUNB0196700','2026-06-28 09:23:43'),(766,'053214573006','HSBC0110004','2026-06-28 09:23:43'),(767,'630702010002657','UBIN0566543','2026-06-28 09:23:43'),(768,'35556575921','SBIN0000621','2026-06-28 09:23:43'),(769,'32009572201','SBIN0009111','2026-06-28 09:23:43'),(770,'10171269712','IDFB0060132','2026-06-28 09:23:43'),(771,'10852336720','SBIN0001868','2026-06-28 09:23:43'),(772,'418001503077','ICIC0004180','2026-06-28 09:23:43'),(773,'45580100010794','BARB0GOVGHA','2026-06-28 09:23:44'),(774,'6304870217','IDIB000N047','2026-06-28 09:23:44'),(775,'8901460002717','HDFC0000890','2026-06-28 09:23:44'),(776,'42931228584','SBIN0031159','2026-06-28 09:23:44'),(777,'34590100001169','BARB0TIJARA','2026-06-28 09:23:44'),(778,'00000051113608478','SBIN0001274','2026-06-28 09:23:44'),(779,'05891050267809','HDFC0004363','2026-06-28 09:23:44'),(780,'10199658956','IDFB0022411','2026-06-28 09:23:44'),(781,'38508000413','SBIN0008435','2026-06-28 09:23:44'),(782,'20076014392','SBIN0001070','2026-06-28 09:23:44'),(783,'061001551392','ICIC0000610','2026-06-28 09:23:45'),(784,'4686001700128014','PUNB0468600','2026-06-28 09:23:45'),(785,'520101254642105','UBIN0919977','2026-06-28 09:23:45'),(786,'10217529489','IDFB0021332','2026-06-28 09:23:45'),(787,'3692429687','CBIN0282225','2026-06-28 09:23:45'),(788,'125701001816','ICIC0001257','2026-06-28 09:23:45'),(789,'32459371465','SBIN0005331','2026-06-28 09:23:45'),(790,'6746452264','KKBK0004587','2026-06-28 09:23:45'),(791,'20207829241','SBIN0000823','2026-06-28 09:23:45'),(792,'924010073503916','UTIB0004294','2026-06-28 09:23:45'),(793,'50100750265360','HDFC0002839','2026-06-28 09:23:45'),(794,'7104066486','IDIB000M055','2026-06-28 09:23:45'),(795,'50100266722413','HDFC0000061','2026-06-28 09:23:46'),(796,'3546441030','KKBK0005632','2026-06-28 09:23:46'),(797,'2900101007785','CNRB0002900','2026-06-28 09:23:46'),(798,'135401523203','ICIC0001354','2026-06-28 09:23:46'),(799,'212201503747','ICIC0002122','2026-06-28 09:23:46'),(800,'1513000102253090','PUNB0151300','2026-06-28 09:23:46'),(801,'75097399809','BARB0BUPGBX','2026-06-28 09:23:46'),(802,'629801119842','ICIC0006298','2026-06-28 09:23:46'),(803,'3343110080051510','UJVN0003343','2026-06-28 09:23:46');
/*!40000 ALTER TABLE `employeepaymentdetails` ENABLE KEYS */;

--
-- Table structure for table `employeepersonaldetails`
--

DROP TABLE IF EXISTS `employeepersonaldetails`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `employeepersonaldetails` (
  `EmployeeId` int NOT NULL,
  `DateOfBirth` varchar(30) NOT NULL DEFAULT '',
  `Mobile` varchar(50) NOT NULL DEFAULT '',
  `PanNumber` varchar(50) NOT NULL DEFAULT '',
  `AadhaarNumber` varchar(50) NOT NULL DEFAULT '',
  `UanNumber` varchar(50) NOT NULL DEFAULT '',
  `EsicNumber` varchar(50) NOT NULL DEFAULT '',
  `Source` varchar(120) NOT NULL DEFAULT '',
  `SourceLocation` varchar(200) NOT NULL DEFAULT '',
  `City` varchar(100) NOT NULL DEFAULT '',
  `District` varchar(100) NOT NULL DEFAULT '',
  `State` varchar(100) NOT NULL DEFAULT '',
  `RawDesignation` varchar(160) NOT NULL DEFAULT '',
  `OriginalEmployeeCode` varchar(80) NOT NULL DEFAULT '',
  `DuplicateResolution` varchar(500) NOT NULL DEFAULT '',
  `ExcelRow` int NOT NULL DEFAULT '0',
  `EsicEmployee` decimal(18,4) NOT NULL DEFAULT '0.0000',
  `PtLwfWorkmenComp` decimal(18,4) NOT NULL DEFAULT '0.0000',
  `Tds` decimal(18,4) NOT NULL DEFAULT '0.0000',
  `Recovery` decimal(18,4) NOT NULL DEFAULT '0.0000',
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`EmployeeId`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `employeepersonaldetails`
--

/*!40000 ALTER TABLE `employeepersonaldetails` DISABLE KEYS */;
INSERT INTO `employeepersonaldetails` VALUES (595,'','','','','','','','','','','','','','',0,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(596,'','','','','','','','','','','','','','',0,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(597,'','','','','','','','','','','','','','',0,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(598,'','','','','','','','','','','','','','',0,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(599,'','','','','','','','','','','','','','',0,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(600,'','','','','','','','','','','','','','',0,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(601,'','','','','','','','','','','','','','',0,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(602,'','','','','','','','','','','','','','',0,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(603,'','','','','','','','','','','','','','',0,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(604,'','','','','','','','','','','','','','',0,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(615,'','','','','','','','','','','','','','',0,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(616,'','','','','','','','','','','','','','',0,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(676,'','','','','','','','','','','','','','',0,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(709,'','8904465176','','','101712457003','','RECL DATA.xlsx','Bangalore','Bengaluru','Bengaluru Urban','Karnataka','Project Engineer','','',3,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(710,'','99167 09799','','','','','RECL DATA.xlsx','Bangalore','Bengaluru','Bengaluru Urban','Karnataka','Project Engineer IT','','',4,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(711,'','77607 43141','','','','','RECL DATA.xlsx','Bangalore','Bengaluru','Bengaluru Urban','Karnataka','Project Engineer','','',5,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(712,'','9448093859','','275212073451','102280436040','','RECL DATA.xlsx','Bangalore','Bengaluru','Bengaluru Urban','Karnataka','Project Engineer','','',6,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:38'),(713,'','9396968800','','213648998005','101473428851','','RECL DATA.xlsx','Vijayawada','Vijayawada','NTR','Andhra Pradesh','Executive Assistant','','',50,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:39'),(714,'','7218768624','GYKPP1150E','467205445207','101839730387','','RECL DATA.xlsx','Bhopal','Bhopal','Bhopal','Madhya Pradesh','Office Attendant','','',8,0.0000,167.0000,0.0000,0.0000,'2026-06-28 09:23:39'),(715,'','7999830478','BOXPY2672H','746559394685','101667619230','','RECL DATA.xlsx','Bhopal','Bhopal','Bhopal','Madhya Pradesh','Project Engineer -IT','','',9,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:39'),(716,'','9094684045','','789878714138','102288676879','','RECL DATA.xlsx','Chennai','Chennai','Chennai','Tamil Nadu','Project Engineer','','',10,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:39'),(717,'','8761088016','ARLPB4203E','933905332000','','','RECL DATA.xlsx','GUWAHATI','Guwahati','Kamrup Metropolitan','Assam','Office Attendant','','',11,0.0000,208.0000,0.0000,0.0000,'2026-06-28 09:23:39'),(718,'','9782632785','','','','','RECL DATA.xlsx','JAIPUR','Jaipur','Jaipur','Rajasthan','office attendent','','',12,0.0000,208.0000,0.0000,0.0000,'2026-06-28 09:23:39'),(719,'','7073133074','','','','','RECL DATA.xlsx','JAIPUR','Jaipur','Jaipur','Rajasthan','office attendent','','',13,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:39'),(720,'','9906330691','DLOPK9531A','614349308604','101375456550','','RECL DATA.xlsx','JAMMU','Jammu','Jammu','Jammu u0026 Kashmir','PEON','','',14,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:39'),(721,'','7006530362','','566056180609','101556862361','','RECL DATA.xlsx','JAMMU','Jammu','Jammu','Jammu u0026 Kashmir','Project Engineer-IT','','',15,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:39'),(722,'','9796030180','','21446663636','100921361376','','RECL DATA.xlsx','JAMMU','Jammu','Jammu','Jammu u0026 Kashmir','Project Engineer','','',16,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:39'),(723,'','7006636964','','648402819539','101502923329','','RECL DATA.xlsx','JAMMU','Jammu','Jammu','Jammu u0026 Kashmir','Project Engineer','','',17,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:39'),(724,'','8013474729','CLWPP0315A','4822 0495 1994','101145485719','4118829110','RECL DATA.xlsx','KOLKATA','Kolkata','Kolkata','West Bengal','Office Attendant','','',18,0.0000,150.0000,0.0000,0.0000,'2026-06-28 09:23:39'),(725,'','9064731606','FWRPR9408H','4450 1527 1664','101865009007','4118859478','RECL DATA.xlsx','KOLKATA','Kolkata','Kolkata','West Bengal','Office Attendant','','',19,0.0000,150.0000,0.0000,0.0000,'2026-06-28 09:23:39'),(726,'','6290451641','CPTPP2173R','9560 3063 8230','101814084179','NA','RECL DATA.xlsx','KOLKATA','Kolkata','Kolkata','West Bengal','Executive Assistant','','',20,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:40'),(727,'','7905855457','BNUPP5603G','679038057184','100601305435','','RECL DATA.xlsx','Lucknow','Lucknow','Lucknow','Uttar Pradesh','Executive Assistant','','',21,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:40'),(728,'','8765642153','LUBPS8633K','505668548481','101769969028','','RECL DATA.xlsx','Lucknow','Lucknow','Lucknow','Uttar Pradesh','Jr. Accountant','','',22,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:40'),(729,'','8793935590','CCFPN8167A','814572894313','101750259106','3517264589','RECL DATA.xlsx','Mumbai','Mumbai','Mumbai','Maharashtra','Peon','','',23,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:40'),(730,'','9463184133','CFWPS1321D','210578861446','100601546009','NA','RECL DATA.xlsx','Panchkula','Panchkula','Panchkula','Haryana','Project Associate(Fin.)','','',24,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:40'),(731,'','','','','','','RECL DATA.xlsx','Panchkula','Panchkula','Panchkula','Haryana','Project Engineer','','',25,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:40'),(732,'','8873491234','CJVPS5464E','980287423593','101558571977','4216106727','RECL DATA.xlsx','Patna','Patna','Patna','Bihar','Peon','','',26,0.0000,83.0000,0.0000,0.0000,'2026-06-28 09:23:40'),(733,'','9771111357','','817508038232','','','RECL DATA.xlsx','Patna','Patna','Patna','Bihar','Peon','','',27,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:40'),(734,'','6201548077','','380378999935','','','RECL DATA.xlsx','Patna','Patna','Patna','Bihar','Project Engineer IT','','',28,0.0000,167.0000,0.0000,0.0000,'2026-06-28 09:23:40'),(735,'','9977415453','BJGPV9557D','607975766095','101253893051','NA','RECL DATA.xlsx','RAIPUR','Raipur','Raipur','Chhattisgarh','Executive Assistant','','',29,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:40'),(736,'','7974266236','BXIPV4431Q','973183485172','101839731023','NA','RECL DATA.xlsx','RAIPUR','Raipur','Raipur','Chhattisgarh','Operations assistant','','',30,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:40'),(737,'','7803997992','GQHPS6854K','931934873280','101790940339','NA','RECL DATA.xlsx','RAIPUR','Raipur','Raipur','Chhattisgarh','Office Attendant/Peon','','',31,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:40'),(738,'','','','','','','RECL DATA.xlsx','RAIPUR','Raipur','Raipur','Chhattisgarh','Office Attendant','','',32,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:40'),(739,'','8878333039','CCBPS9435C','253944894281','101201081797','NA','RECL DATA.xlsx','RAIPUR','Raipur','Raipur','Chhattisgarh','Project Associate(Fin.)','','',33,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:41'),(740,'','8602205604','CRWPD4040A','564767110190','NA','NA','RECL DATA.xlsx','RAIPUR','Raipur','Raipur','Chhattisgarh','Project Associate(Fin.)','','',34,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:41'),(741,'','8305706245','','920245429590','NA','NA','RECL DATA.xlsx','RAIPUR','Raipur','Raipur','Chhattisgarh','MTS','','',35,99.9400,0.0000,0.0000,0.0000,'2026-06-28 09:23:41'),(742,'','8109698310','','968744065321','','','RECL DATA.xlsx','RAIPUR','Raipur','Raipur','Chhattisgarh','Project Engineer','','',36,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:41'),(743,'','9098795235','','373812900006','','','RECL DATA.xlsx','RAIPUR','Raipur','Raipur','Chhattisgarh','Project Engineer','','',37,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:41'),(744,'','','','','','','RECL DATA.xlsx','Ranchi','Ranchi','Ranchi','Jharkhand','Junior Accountant.','','',38,0.0000,150.0000,0.0000,0.0000,'2026-06-28 09:23:41'),(745,'','7739769734','','924780097009','','','RECL DATA.xlsx','Ranchi','Ranchi','Ranchi','Jharkhand','Project Engineer','','',39,0.0000,150.0000,0.0000,0.0000,'2026-06-28 09:23:41'),(746,'','8580810945','JWJPS1099K','417970459941','','','RECL DATA.xlsx','Shimla','Shimla','Shimla','Himachal Pradesh','Executive Assistant','','',40,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:41'),(747,'','9805274075','GFDPK1228E','258729851504','','','RECL DATA.xlsx','Shimla','Shimla','Shimla','Himachal Pradesh','Project Engineer','','',41,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:41'),(748,'','9633202294','GNEPS8034P','914020315657','NIL','NA','RECL DATA.xlsx','Thiruvananthapuram','Thiruvananthapuram','Thiruvananthapuram','Kerala','Executive Assistant','','',42,0.0000,300.0000,0.0000,0.0000,'2026-06-28 09:23:41'),(749,'','8129837740','FZTPR1592H','694573207938','101814084198','NA','RECL DATA.xlsx','Thiruvananthapuram','Thiruvananthapuram','Thiruvananthapuram','Kerala','Jr. Accountant','','',43,0.0000,450.0000,0.0000,0.0000,'2026-06-28 09:23:41'),(750,'','7025774162','FCNPA5667K','633880733968','101955961338','NA','RECL DATA.xlsx','Thiruvananthapuram','Thiruvananthapuram','Thiruvananthapuram','Kerala','Jr. Accountant','','',44,0.0000,450.0000,0.0000,0.0000,'2026-06-28 09:23:42'),(751,'','9544787114','BQGPN0076A','537283751692','101934104603','4709004989','RECL DATA.xlsx','Thiruvananthapuram','Thiruvananthapuram','Thiruvananthapuram','Kerala','Office Attendant/Peon','','',45,0.0000,180.0000,0.0000,0.0000,'2026-06-28 09:23:42'),(752,'','9020625656','','739234015968','','','RECL DATA.xlsx','Thiruvananthapuram','Thiruvananthapuram','Thiruvananthapuram','Kerala','Project Engineer','','',46,0.0000,450.0000,0.0000,0.0000,'2026-06-28 09:23:42'),(753,'','8281383944','','871557056182','','','RECL DATA.xlsx','Thiruvananthapuram','Thiruvananthapuram','Thiruvananthapuram','Kerala','Project Engineer','','',47,0.0000,450.0000,0.0000,0.0000,'2026-06-28 09:23:42'),(754,'','8285252961','','543855632209','','','RECL DATA.xlsx','Vadodra','Vadodara','Vadodara','Gujarat','PROJECT ENGINEER','','',48,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:42'),(755,'','8871458438','','832289097861','','','RECL DATA.xlsx','Vadodra','Vadodara','Vadodara','Gujarat','Project Engineer (IT)','','',49,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:42'),(757,'','9490384276','','315256907180','','','RECL DATA.xlsx','Vijayawada','Vijayawada','NTR','Andhra Pradesh','Project Engineer - IT','','',51,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:42'),(758,'','8248544506','','341961287434','','','RECL DATA.xlsx','Vijayawada','Vijayawada','NTR','Andhra Pradesh','Project Engineer','','',52,0.0000,200.0000,0.0000,0.0000,'2026-06-28 09:23:42'),(759,'','8737010861','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',53,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:42'),(760,'','9034517060','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',54,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:42'),(761,'','8933991441','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',55,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:42'),(762,'','7042216233','','','','','RECL DATA.xlsx','SCOPE Complex, New Delhi','New Delhi','New Delhi','Delhi','Project Engineer','','',56,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:43'),(763,'','9910800825','','','','','RECL DATA.xlsx','SCOPE Complex, New Delhi','New Delhi','New Delhi','Delhi','Project Engineer','','',57,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:43'),(764,'','8929990694','','','','','RECL DATA.xlsx','SCOPE Complex, New Delhi','New Delhi','New Delhi','Delhi','Project Engineer','','',58,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:43'),(765,'','7004820951','','','','','RECL DATA.xlsx','SCOPE Complex, New Delhi','New Delhi','New Delhi','Delhi','Project Engineer','','',59,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:43'),(766,'','7479787391','','','','','RECL DATA.xlsx','SCOPE Complex, New Delhi','New Delhi','New Delhi','Delhi','Project Engineer','','',60,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:43'),(767,'','9882159014','','','','','RECL DATA.xlsx','SCOPE Complex, New Delhi','New Delhi','New Delhi','Delhi','Project Engineer','','',61,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:43'),(768,'','7991621535','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',62,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:43'),(769,'','9899998253','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',63,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:43'),(770,'','8287496690','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',64,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:43'),(771,'','9415582398','','','','','RECL DATA.xlsx','MNRE','New Delhi','New Delhi','Delhi','IT/Data Consultant','','',65,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:43'),(772,'','9911743570','','','','','RECL DATA.xlsx','SCOPE Complex, New Delhi','New Delhi','New Delhi','Delhi','Project Engineer (IT)','','',66,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:43'),(773,'','8588977843','','','','','RECL DATA.xlsx','MNRE','New Delhi','New Delhi','Delhi','Project Engineer (IT)','','',67,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:44'),(774,'','7252043705','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',68,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:44'),(775,'','8980643661','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',69,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:44'),(776,'','9950253281','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',70,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:44'),(777,'','7339877730','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',71,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:44'),(778,'','8744838417','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',72,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:44'),(779,'','9582336006','','','','','RECL DATA.xlsx','MNRE','New Delhi','New Delhi','Delhi','IT/Data Consultant','','',73,0.0000,0.0000,5200.0000,0.0000,'2026-06-28 09:23:44'),(780,'','7895748295','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',74,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:44'),(781,'','8871865751','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',75,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:44'),(782,'','9857500740','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',76,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:44'),(783,'','8285832169','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',77,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:45'),(784,'','8787255571','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',78,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:45'),(785,'','8210341552','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',79,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:45'),(786,'','7011934003','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',80,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:45'),(787,'','7607211994','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',81,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:45'),(788,'','9927683660','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',82,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:45'),(789,'','9304827625','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',83,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:45'),(790,'','7042170885','','','','','RECL DATA.xlsx','MNRE','New Delhi','New Delhi','Delhi','Project Engineer','','',84,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:45'),(791,'','','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',85,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:45'),(792,'','','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',86,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:45'),(793,'','8384857143','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',87,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:45'),(794,'','7505556382','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',88,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:45'),(795,'','','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',89,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:46'),(796,'','','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',90,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:46'),(797,'','','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Project Engineer','','',91,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:46'),(798,'','8383895354','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Home Office, MoP','','',92,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:46'),(799,'','8076316422','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Home Office, MoP','','',93,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:46'),(800,'','9810986131','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','HR','','',94,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:46'),(801,'','','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','HR','','',95,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:46'),(802,'','','','','','','RECL DATA.xlsx','CO, Gurugram','Gurugram','Gurugram','Haryana','Operations Assistant','','',96,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:46'),(803,'','6372950299','','758050871042','','','RECL DATA.xlsx','BBN','Baddi-Barotiwala-Nalagarh','Solan','Himachal Pradesh','Jr. Accontant','RECL171','Seeded as RECL171-BBN because source has duplicate RECL171 at rows 7 and 50',7,0.0000,0.0000,0.0000,0.0000,'2026-06-28 09:23:46');
/*!40000 ALTER TABLE `employeepersonaldetails` ENABLE KEYS */;

--
-- Table structure for table `employees`
--

DROP TABLE IF EXISTS `employees`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `employees` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ClientId` int NOT NULL,
  `EmployeeCode` varchar(50) NOT NULL,
  `FirstName` varchar(100) NOT NULL,
  `LastName` varchar(100) DEFAULT NULL,
  `Gender` varchar(30) DEFAULT NULL,
  `DateOfJoining` varchar(30) DEFAULT NULL,
  `WorkEmail` varchar(150) DEFAULT NULL,
  `Department` varchar(100) DEFAULT NULL,
  `Designation` varchar(100) DEFAULT NULL,
  `WorkLocationId` int NOT NULL DEFAULT '0',
  `ReportingManagerId` int NOT NULL DEFAULT '0',
  `PortalAccess` tinyint(1) NOT NULL DEFAULT '0',
  `SalaryStructureId` varchar(50) DEFAULT NULL,
  `AnnualCtc` decimal(18,2) NOT NULL DEFAULT '0.00',
  `SalaryJson` json NOT NULL,
  `PersonalJson` json NOT NULL,
  `PaymentJson` json NOT NULL,
  `IsActive` tinyint(1) NOT NULL DEFAULT '1',
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_Employees_Client_Code` (`ClientId`,`EmployeeCode`)
) ENGINE=InnoDB AUTO_INCREMENT=834 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `employees`
--

/*!40000 ALTER TABLE `employees` DISABLE KEYS */;
INSERT INTO `employees` VALUES (595,7,'ACME001','Amit','Verma','Male','2026-04-01','amit.verma@acme.test','Engineering','Software Engineer',1,0,0,'',900000.00,'{}','{}','{}',1,'2026-06-25 22:18:56','2026-06-25 22:33:51'),(596,1,'ACME002','Neha','Kapoor','Female','2026-04-01','neha.kapoor@acme.test','Engineering','Software Engineer',1,0,0,'201',840000.00,'{\"101\": \"28000\", \"102\": \"14000\", \"103\": \"22000\", \"104\": \"1800\"}','{}','{}',1,'2026-06-25 22:18:56','2026-06-25 22:18:56'),(597,1,'ACME003','Rahul','Singh','Male','2026-04-01','rahul.singh@acme.test','Finance','Payroll Executive',1,0,0,'201',720000.00,'{\"101\": \"24000\", \"102\": \"12000\", \"103\": \"18000\", \"104\": \"1800\"}','{}','{}',1,'2026-06-25 22:18:56','2026-06-25 22:18:56'),(598,1,'ACME004','Priya','Nair','Female','2026-04-01','priya.nair@acme.test','HR','Manager',1,0,0,'201',960000.00,'{\"101\": \"32000\", \"102\": \"16000\", \"103\": \"27000\", \"104\": \"1800\"}','{}','{}',1,'2026-06-25 22:18:56','2026-06-25 22:18:56'),(599,1,'ACME005','Vikram','Das','Male','2026-04-01','vikram.das@acme.test','Engineering','Software Engineer',1,0,0,'201',780000.00,'{\"101\": \"26000\", \"102\": \"13000\", \"103\": \"20000\", \"104\": \"1800\"}','{}','{}',1,'2026-06-25 22:18:56','2026-06-25 22:18:56'),(600,7,'NORTH001','Ananya','Rao','Female','2026-04-01','ananya.rao@north.test','Engineering','Software Engineer',2,0,0,'',900000.00,'{}','{}','{}',1,'2026-06-25 22:18:56','2026-06-25 22:34:07'),(601,2,'NORTH002','Karan','Mehta','Male','2026-04-01','karan.mehta@north.test','Finance','Payroll Executive',2,0,0,'',750000.00,'{\"101\": \"25000\", \"102\": \"12500\", \"103\": \"19000\", \"104\": \"1800\"}','{}','{}',1,'2026-06-25 22:18:56','2026-06-25 22:18:56'),(602,2,'NORTH003','Sneha','Iyer','Female','2026-04-01','sneha.iyer@north.test','HR','Manager',2,0,0,'',840000.00,'{\"101\": \"28000\", \"102\": \"14000\", \"103\": \"22000\", \"104\": \"1800\"}','{}','{}',1,'2026-06-25 22:18:56','2026-06-25 22:18:56'),(603,2,'NORTH004','Arjun','Malik','Male','2026-04-01','arjun.malik@north.test','Engineering','Software Engineer',2,0,0,'',810000.00,'{\"101\": \"27000\", \"102\": \"13500\", \"103\": \"21000\", \"104\": \"1800\"}','{}','{}',1,'2026-06-25 22:18:56','2026-06-25 22:18:56'),(604,2,'NORTH005','Meera','Shah','Female','2026-04-01','meera.shah@north.test','Finance','Payroll Executive',2,0,0,'',870000.00,'{\"101\": \"29000\", \"102\": \"14500\", \"103\": \"23000\", \"104\": \"1800\"}','{}','{}',1,'2026-06-25 22:18:56','2026-06-25 22:18:56'),(615,1,'ACME001','Amit','Verma','Male','2026-04-01','amit.verma@acme.test','Engineering','Software Engineer',1,0,0,'201',900000.00,'{\"101\": \"30000\", \"102\": \"15000\", \"103\": \"25000\", \"104\": \"1800\"}','{}','{}',1,'2026-06-25 23:21:46','2026-06-25 23:21:46'),(616,2,'NORTH001','Ananya','Rao','Female','2026-04-01','ananya.rao@north.test','Engineering','Software Engineer',2,0,0,'',900000.00,'{\"101\": \"30000\", \"102\": \"15000\", \"103\": \"25000\", \"104\": \"1800\"}','{}','{}',1,'2026-06-25 23:21:46','2026-06-25 23:21:46'),(676,7,'APPC0023','Rishav','Mishra','Male','2026-06-03','mishra@frevo.com','Engineering','Payroll Executive',44,0,1,'',0.00,'{}','{}','{}',1,'2026-06-26 17:32:52','2026-06-26 17:32:52'),(709,10,'RECL373','Vinuth','TN','','2024-08-30',NULL,'RECL','Project Engineer',46,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Bengaluru\", \"state\": \"Karnataka\", \"mobile\": \"8904465176\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Bengaluru Urban\", \"excelRow\": 3, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"101712457003\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"Bangalore\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"SBIN0005198\", \"bankAccountNo\": \"33722161464\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(710,10,'RECL451','BHAVISH','SHARMA','','2025-03-03',NULL,'RECL','Project Engineer IT',46,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Bengaluru\", \"state\": \"Karnataka\", \"mobile\": \"99167 09799\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Bengaluru Urban\", \"excelRow\": 4, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer IT\", \"sourceLocation\": \"Bangalore\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"BARB0BELTHA\", \"bankAccountNo\": \"45150100004044\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(711,10,'RECL456','NETRAVATI',NULL,'','2025-03-03',NULL,'RECL','Project Engineer',46,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Bengaluru\", \"state\": \"Karnataka\", \"mobile\": \"77607 43141\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Bengaluru Urban\", \"excelRow\": 5, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"Bangalore\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"CNRB0000457\", \"bankAccountNo\": \"0457120000041\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(712,10,'RECL510','Varsha','Patil','','2025-12-08','varshapatil9698@gmail.com','RECL','Project Engineer',46,0,0,'9202',600000.00,'{\"101\": 25000, \"102\": 10000, \"103\": 2000, \"104\": 2082, \"105\": 1250, \"106\": 9668, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 50000}','{\"tds\": 0, \"city\": \"Bengaluru\", \"state\": \"Karnataka\", \"mobile\": \"9448093859\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Bengaluru Urban\", \"excelRow\": 6, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"102280436040\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"275212073451\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"Bangalore\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"SBIN0040702\", \"bankAccountNo\": \"38991250870\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(713,10,'RECL171','Varre','Sony Gopika','','2024-04-26','sonygopika@gmail.com','RECL','Executive Assistant',66,0,0,'9203',546000.00,'{\"101\": 22750, \"102\": 9100, \"103\": 1000, \"104\": 1895, \"105\": 1250, \"106\": 9505, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 45500}','{\"tds\": 0, \"city\": \"Vijayawada\", \"state\": \"Andhra Pradesh\", \"mobile\": \"9396968800\", \"source\": \"RECL DATA.xlsx\", \"district\": \"NTR\", \"excelRow\": 50, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"101473428851\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"213648998005\", \"rawDesignation\": \"Executive Assistant\", \"sourceLocation\": \"Vijayawada\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"HDFC0009088\", \"bankAccountNo\": \"50100156871214\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(714,10,'RECL010','Mahendra','Panda','','2024-03-28','mahendrapanda99030@gmail.com','RECL','Office Attendant',48,0,0,'9203',312000.00,'{\"101\": 13000, \"102\": 5200, \"103\": 1000, \"104\": 1082.9, \"105\": 1250, \"106\": 4467, \"107\": 0, \"108\": 0, \"109\": 1560, \"GROSS\": 26000}','{\"tds\": 0, \"city\": \"Bhopal\", \"state\": \"Madhya Pradesh\", \"mobile\": \"7218768624\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Bhopal\", \"excelRow\": 8, \"recovery\": 0, \"panNumber\": \"GYKPP1150E\", \"uanNumber\": \"101839730387\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"467205445207\", \"rawDesignation\": \"Office Attendant\", \"sourceLocation\": \"Bhopal\", \"ptLwfWorkmenComp\": 167}','{\"ifscCode\": \"IDIB000G544\", \"bankAccountNo\": \"7218768624\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(715,10,'RECL278','Aparna','Tiwari','','2024-07-15','Aparnatiwari215@gmail.com','RECL','Project Engineer IT',48,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Bhopal\", \"state\": \"Madhya Pradesh\", \"mobile\": \"7999830478\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Bhopal\", \"excelRow\": 9, \"recovery\": 0, \"panNumber\": \"BOXPY2672H\", \"uanNumber\": \"101667619230\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"746559394685\", \"rawDesignation\": \"Project Engineer -IT\", \"sourceLocation\": \"Bhopal\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"UTIB0000512\", \"bankAccountNo\": \"923010050649438\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(716,10,'RECL511','Prabakaran',NULL,'','2026-01-16',NULL,'RECL','Project Engineer',49,0,0,'9202',600000.00,'{\"101\": 25000, \"102\": 10000, \"103\": 2000, \"104\": 2082, \"105\": 1250, \"106\": 9668, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 50000}','{\"tds\": 0, \"city\": \"Chennai\", \"state\": \"Tamil Nadu\", \"mobile\": \"9094684045\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Chennai\", \"excelRow\": 10, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"102288676879\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"789878714138\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"Chennai\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"BARB0VILLUP\", \"bankAccountNo\": \"33100100025019\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(717,10,'RECL144','BANKIM','BRAHMA','','2024-04-03','bbankim8316@gmail.com','RECL','Office Attendant',51,0,0,'9203',312000.00,'{\"101\": 13000, \"102\": 5200, \"103\": 1000, \"104\": 1082.9, \"105\": 1250, \"106\": 4467, \"107\": 0, \"108\": 0, \"109\": 1560, \"GROSS\": 26000}','{\"tds\": 0, \"city\": \"Guwahati\", \"state\": \"Assam\", \"mobile\": \"8761088016\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Kamrup Metropolitan\", \"excelRow\": 11, \"recovery\": 0, \"panNumber\": \"ARLPB4203E\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"933905332000\", \"rawDesignation\": \"Office Attendant\", \"sourceLocation\": \"GUWAHATI\", \"ptLwfWorkmenComp\": 208}','{\"ifscCode\": \"SBIN0003030\", \"bankAccountNo\": \"20120947998\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(718,10,'RECL153','Meetha','lal Meena','','2024-04-01',NULL,'RECL','Office Attendant',52,0,0,'9203',312000.00,'{\"101\": 13000, \"102\": 5200, \"103\": 1000, \"104\": 1082.9, \"105\": 1250, \"106\": 4467, \"107\": 0, \"108\": 0, \"109\": 1560, \"GROSS\": 26000}','{\"tds\": 0, \"city\": \"Jaipur\", \"state\": \"Rajasthan\", \"mobile\": \"9782632785\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Jaipur\", \"excelRow\": 12, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"office attendent\", \"sourceLocation\": \"JAIPUR\", \"ptLwfWorkmenComp\": 208}','{\"ifscCode\": \"SBIN0031595\", \"bankAccountNo\": \"40573917888\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(719,10,'RECL154','Raju','Lal Meena','','2024-04-01',NULL,'RECL','Office Attendant',52,0,0,'9203',312000.00,'{\"101\": 13000, \"102\": 5200, \"103\": 1000, \"104\": 1082.9, \"105\": 1250, \"106\": 4467, \"107\": 0, \"108\": 0, \"109\": 1560, \"GROSS\": 26000}','{\"tds\": 0, \"city\": \"Jaipur\", \"state\": \"Rajasthan\", \"mobile\": \"7073133074\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Jaipur\", \"excelRow\": 13, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"office attendent\", \"sourceLocation\": \"JAIPUR\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"BKID0006603\", \"bankAccountNo\": \"660318210009719\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(720,10,'REC135','SURJEET','KUMAR','','2024-04-04','surjeetpdcl123@gmail.com','RECL','Peon',53,0,0,'9203',312000.00,'{\"101\": 13000, \"102\": 5200, \"103\": 1000, \"104\": 1082.9, \"105\": 1250, \"106\": 4467, \"107\": 0, \"108\": 0, \"109\": 1560, \"GROSS\": 26000}','{\"tds\": 0, \"city\": \"Jammu\", \"state\": \"Jammu u0026 Kashmir\", \"mobile\": \"9906330691\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Jammu\", \"excelRow\": 14, \"recovery\": 0, \"panNumber\": \"DLOPK9531A\", \"uanNumber\": \"101375456550\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"614349308604\", \"rawDesignation\": \"PEON\", \"sourceLocation\": \"JAMMU\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0008547\", \"bankAccountNo\": \"20239611421\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(721,10,'REC237','Sheetal','Mahajan','','2024-07-15','sheetalmahajan57@gmail.com','RECL','Project Engineer IT',53,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Jammu\", \"state\": \"Jammu u0026 Kashmir\", \"mobile\": \"7006530362\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Jammu\", \"excelRow\": 15, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"101556862361\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"566056180609\", \"rawDesignation\": \"Project Engineer-IT\", \"sourceLocation\": \"JAMMU\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"HDFC0002758\", \"bankAccountNo\": \"50100436294491\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(722,10,'REC230','Shemaila','Aslam','','2024-07-15','aslam.shemaila@yahoo.com','RECL','Project Engineer',53,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Jammu\", \"state\": \"Jammu u0026 Kashmir\", \"mobile\": \"9796030180\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Jammu\", \"excelRow\": 16, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"100921361376\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"21446663636\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"JAMMU\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"ICIC0000407\", \"bankAccountNo\": \"040701516329\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(723,10,'REC229','Shavindu','Mehta','','2024-07-15','shavindumehta@gmail.com','RECL','Project Engineer',53,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Jammu\", \"state\": \"Jammu u0026 Kashmir\", \"mobile\": \"7006636964\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Jammu\", \"excelRow\": 17, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"101502923329\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"648402819539\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"JAMMU\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"SBIN0011857\", \"bankAccountNo\": \"20147235426\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(724,10,'RECL140','Amol','Pal','','2024-04-03','amolpal269@gmail.com','RECL','Office Attendant',54,0,0,'9203',312000.00,'{\"101\": 13000, \"102\": 5200, \"103\": 1000, \"104\": 1082.9, \"105\": 1250, \"106\": 4467, \"107\": 0, \"108\": 0, \"109\": 1560, \"GROSS\": 26000}','{\"tds\": 0, \"city\": \"Kolkata\", \"state\": \"West Bengal\", \"mobile\": \"8013474729\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Kolkata\", \"excelRow\": 18, \"recovery\": 0, \"panNumber\": \"CLWPP0315A\", \"uanNumber\": \"101145485719\", \"esicNumber\": \"4118829110\", \"esicEmployee\": 0, \"aadhaarNumber\": \"4822 0495 1994\", \"rawDesignation\": \"Office Attendant\", \"sourceLocation\": \"KOLKATA\", \"ptLwfWorkmenComp\": 150}','{\"ifscCode\": \"SBIN0007679\", \"bankAccountNo\": \"20248664620\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(725,10,'RECL141','Jagannath','Rajak','','2024-04-03','jagannathrajak4554@gmail.com','RECL','Office Attendant',54,0,0,'9203',312000.00,'{\"101\": 13000, \"102\": 5200, \"103\": 1000, \"104\": 1082.9, \"105\": 1250, \"106\": 4467, \"107\": 0, \"108\": 0, \"109\": 1560, \"GROSS\": 26000}','{\"tds\": 0, \"city\": \"Kolkata\", \"state\": \"West Bengal\", \"mobile\": \"9064731606\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Kolkata\", \"excelRow\": 19, \"recovery\": 0, \"panNumber\": \"FWRPR9408H\", \"uanNumber\": \"101865009007\", \"esicNumber\": \"4118859478\", \"esicEmployee\": 0, \"aadhaarNumber\": \"4450 1527 1664\", \"rawDesignation\": \"Office Attendant\", \"sourceLocation\": \"KOLKATA\", \"ptLwfWorkmenComp\": 150}','{\"ifscCode\": \"SBIN0009125\", \"bankAccountNo\": \"37895364374\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(726,10,'RECL138','Saraswati','Paul','','2024-04-03','saraswati.paul14@gmail.com','RECL','Executive Assistant',54,0,0,'9203',504000.00,'{\"101\": 21000, \"102\": 8400, \"103\": 1000, \"104\": 1749, \"105\": 1250, \"106\": 8601, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 42000}','{\"tds\": 0, \"city\": \"Kolkata\", \"state\": \"West Bengal\", \"mobile\": \"6290451641\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Kolkata\", \"excelRow\": 20, \"recovery\": 0, \"panNumber\": \"CPTPP2173R\", \"uanNumber\": \"101814084179\", \"esicNumber\": \"NA\", \"esicEmployee\": 0, \"aadhaarNumber\": \"9560 3063 8230\", \"rawDesignation\": \"Executive Assistant\", \"sourceLocation\": \"KOLKATA\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"SBIN0012361\", \"bankAccountNo\": \"20397956804\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(727,10,'RECL019','Awdhesh','Prajapati','','2024-03-28','AWADHESHP09@GMAIL.COM','RECL','Executive Assistant',55,0,0,'9203',504000.00,'{\"101\": 21000, \"102\": 8400, \"103\": 1000, \"104\": 1749, \"105\": 1250, \"106\": 8601, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 42000}','{\"tds\": 0, \"city\": \"Lucknow\", \"state\": \"Uttar Pradesh\", \"mobile\": \"7905855457\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Lucknow\", \"excelRow\": 21, \"recovery\": 0, \"panNumber\": \"BNUPP5603G\", \"uanNumber\": \"100601305435\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"679038057184\", \"rawDesignation\": \"Executive Assistant\", \"sourceLocation\": \"Lucknow\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"SBIN0011213\", \"bankAccountNo\": \"32933760758\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(728,10,'RECL020','Sakshi','Srivastava','','2024-03-28','SAKSHI31SRIVASTAVA@GMAIL.COM','RECL','Junior Accountant',55,0,0,'9203',576000.00,'{\"101\": 24000, \"102\": 9600, \"103\": 1000, \"104\": 1999, \"105\": 1250, \"106\": 10151, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 48000}','{\"tds\": 0, \"city\": \"Lucknow\", \"state\": \"Uttar Pradesh\", \"mobile\": \"8765642153\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Lucknow\", \"excelRow\": 22, \"recovery\": 0, \"panNumber\": \"LUBPS8633K\", \"uanNumber\": \"101769969028\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"505668548481\", \"rawDesignation\": \"Jr. Accountant\", \"sourceLocation\": \"Lucknow\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"HDFC0000722\", \"bankAccountNo\": \"50100275440494\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(729,10,'RECL152','RAHUL','DIPAK NIKAM','','2024-04-10','rahulnikam0145@gmail.com','RECL','Peon',57,0,0,'9203',312000.00,'{\"101\": 13000, \"102\": 5200, \"103\": 1000, \"104\": 1082.9, \"105\": 1250, \"106\": 4467, \"107\": 0, \"108\": 0, \"109\": 1560, \"GROSS\": 26000}','{\"tds\": 0, \"city\": \"Mumbai\", \"state\": \"Maharashtra\", \"mobile\": \"8793935590\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Mumbai\", \"excelRow\": 23, \"recovery\": 0, \"panNumber\": \"CCFPN8167A\", \"uanNumber\": \"101750259106\", \"esicNumber\": \"3517264589\", \"esicEmployee\": 0, \"aadhaarNumber\": \"814572894313\", \"rawDesignation\": \"Peon\", \"sourceLocation\": \"Mumbai\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"UBIN0901067\", \"bankAccountNo\": \"520101265052317\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(730,10,'RECL160','VIKAS','SHARMA','','2024-04-16','vikas.sharma0612@gmail.com','RECL','Project Associate(Fin.)',58,0,0,'9201',720000.00,'{\"101\": 30000, \"102\": 12000, \"103\": 2000, \"104\": 2499, \"105\": 1250, \"106\": 12251, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 60000}','{\"tds\": 0, \"city\": \"Panchkula\", \"state\": \"Haryana\", \"mobile\": \"9463184133\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Panchkula\", \"excelRow\": 24, \"recovery\": 0, \"panNumber\": \"CFWPS1321D\", \"uanNumber\": \"100601546009\", \"esicNumber\": \"NA\", \"esicEmployee\": 0, \"aadhaarNumber\": \"210578861446\", \"rawDesignation\": \"Project Associate(Fin.)\", \"sourceLocation\": \"Panchkula\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0001712\", \"bankAccountNo\": \"35225519635\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(731,10,'REC286','RAMA','SHANKAR','','2024-09-03','ranjeevpoonia@gmail.com','RECL','Project Engineer',58,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Panchkula\", \"state\": \"Haryana\", \"mobile\": \"\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Panchkula\", \"excelRow\": 25, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"Panchkula\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0012302\", \"bankAccountNo\": \"32236412272\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(732,10,'RECL169','Subodh','Kumar Singh','','2024-04-18','subodhsingh1989@gmail.com','RECL','Peon',59,0,0,'9203',312000.00,'{\"101\": 13000, \"102\": 5200, \"103\": 1000, \"104\": 1082.9, \"105\": 1250, \"106\": 4467, \"107\": 0, \"108\": 0, \"109\": 1560, \"GROSS\": 26000}','{\"tds\": 0, \"city\": \"Patna\", \"state\": \"Bihar\", \"mobile\": \"8873491234\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Patna\", \"excelRow\": 26, \"recovery\": 0, \"panNumber\": \"CJVPS5464E\", \"uanNumber\": \"101558571977\", \"esicNumber\": \"4216106727\", \"esicEmployee\": 0, \"aadhaarNumber\": \"980287423593\", \"rawDesignation\": \"Peon\", \"sourceLocation\": \"Patna\", \"ptLwfWorkmenComp\": 83}','{\"ifscCode\": \"SBIN0001435\", \"bankAccountNo\": \"20016364994\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(733,10,'RECL362','Sanjeet','Kumar','','2024-06-06','SANJEETKUMAR011@GMAIL.COM','RECL','Peon',59,0,0,'9203',288000.00,'{\"101\": 12000, \"102\": 4800, \"103\": 1000, \"104\": 999.6, \"105\": 1250, \"106\": 3950, \"107\": 0, \"108\": 0, \"109\": 1440, \"GROSS\": 24000}','{\"tds\": 0, \"city\": \"Patna\", \"state\": \"Bihar\", \"mobile\": \"9771111357\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Patna\", \"excelRow\": 27, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"817508038232\", \"rawDesignation\": \"Peon\", \"sourceLocation\": \"Patna\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"PUNB0757700\", \"bankAccountNo\": \"7577000100052549\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(734,10,'RECL192','Ratnesh','Kumar Bhagat','','2024-07-15','ratneshbhagat2000@gmail.com','RECL','Project Engineer IT',59,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Patna\", \"state\": \"Bihar\", \"mobile\": \"6201548077\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Patna\", \"excelRow\": 28, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"380378999935\", \"rawDesignation\": \"Project Engineer IT\", \"sourceLocation\": \"Patna\", \"ptLwfWorkmenComp\": 167}','{\"ifscCode\": \"SBIN0017109\", \"bankAccountNo\": \"38237856835\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(735,10,'RECL002','DILESH','KUMAR','','2024-03-27','dileshverma1028@gmail.com','RECL','Executive Assistant',60,0,0,'9203',504000.00,'{\"101\": 21000, \"102\": 8400, \"103\": 1000, \"104\": 1749, \"105\": 1250, \"106\": 8601, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 42000}','{\"tds\": 0, \"city\": \"Raipur\", \"state\": \"Chhattisgarh\", \"mobile\": \"9977415453\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Raipur\", \"excelRow\": 29, \"recovery\": 0, \"panNumber\": \"BJGPV9557D\", \"uanNumber\": \"101253893051\", \"esicNumber\": \"NA\", \"esicEmployee\": 0, \"aadhaarNumber\": \"607975766095\", \"rawDesignation\": \"Executive Assistant\", \"sourceLocation\": \"RAIPUR\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0060301\", \"bankAccountNo\": \"37864781555\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(736,10,'RECL003','BHUSHAN','VERMA','','2024-03-27','vbhushan888@gmail.com','RECL','Operations Assistant',60,0,0,'9203',390000.00,'{\"101\": 16250, \"102\": 6500, \"103\": 1000, \"104\": 1353, \"105\": 1250, \"106\": 6147, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 32500}','{\"tds\": 0, \"city\": \"Raipur\", \"state\": \"Chhattisgarh\", \"mobile\": \"7974266236\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Raipur\", \"excelRow\": 30, \"recovery\": 0, \"panNumber\": \"BXIPV4431Q\", \"uanNumber\": \"101839731023\", \"esicNumber\": \"NA\", \"esicEmployee\": 0, \"aadhaarNumber\": \"973183485172\", \"rawDesignation\": \"Operations assistant\", \"sourceLocation\": \"RAIPUR\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"MAHB0000439\", \"bankAccountNo\": \"68011261658\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(737,10,'RECL006','VINAY','KUMAR SAHU','','2024-03-27','vinaysahuvinay2411@gmail.com','RECL','Office Attendant',60,0,0,'9203',312000.00,'{\"101\": 13000, \"102\": 5200, \"103\": 1000, \"104\": 1082.9, \"105\": 1250, \"106\": 4467, \"107\": 0, \"108\": 0, \"109\": 1560, \"GROSS\": 26000}','{\"tds\": 0, \"city\": \"Raipur\", \"state\": \"Chhattisgarh\", \"mobile\": \"7803997992\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Raipur\", \"excelRow\": 31, \"recovery\": 0, \"panNumber\": \"GQHPS6854K\", \"uanNumber\": \"101790940339\", \"esicNumber\": \"NA\", \"esicEmployee\": 0, \"aadhaarNumber\": \"931934873280\", \"rawDesignation\": \"Office Attendant/Peon\", \"sourceLocation\": \"RAIPUR\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"MAHB0001580\", \"bankAccountNo\": \"68011168274\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(738,10,'RECL305','Sunil','Kumar Sahu','','2024-06-26',NULL,'RECL','Office Attendant',60,0,0,'9203',288000.00,'{\"101\": 12000, \"102\": 4800, \"103\": 1000, \"104\": 999.6, \"105\": 1250, \"106\": 3950, \"107\": 0, \"108\": 0, \"109\": 1440, \"GROSS\": 24000}','{\"tds\": 0, \"city\": \"Raipur\", \"state\": \"Chhattisgarh\", \"mobile\": \"\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Raipur\", \"excelRow\": 32, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Office Attendant\", \"sourceLocation\": \"RAIPUR\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"UTIB0SJSD01\", \"bankAccountNo\": \"101008353860\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(739,10,'RECL388','Varun','Singh Gaharwar','','2024-10-16','VARUNSINGHGAHARWAR@GMAIL.COM','RECL','Project Associate(Fin.)',60,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Raipur\", \"state\": \"Chhattisgarh\", \"mobile\": \"8878333039\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Raipur\", \"excelRow\": 33, \"recovery\": 0, \"panNumber\": \"CCBPS9435C\", \"uanNumber\": \"101201081797\", \"esicNumber\": \"NA\", \"esicEmployee\": 0, \"aadhaarNumber\": \"253944894281\", \"rawDesignation\": \"Project Associate(Fin.)\", \"sourceLocation\": \"RAIPUR\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"HDFC0000152\", \"bankAccountNo\": \"50100143405779\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(740,10,'RECL390','Priya','Dembani','','2024-11-11','dembani75@gmail.com','RECL','Project Associate(Fin.)',60,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Raipur\", \"state\": \"Chhattisgarh\", \"mobile\": \"8602205604\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Raipur\", \"excelRow\": 34, \"recovery\": 0, \"panNumber\": \"CRWPD4040A\", \"uanNumber\": \"NA\", \"esicNumber\": \"NA\", \"esicEmployee\": 0, \"aadhaarNumber\": \"564767110190\", \"rawDesignation\": \"Project Associate(Fin.)\", \"sourceLocation\": \"RAIPUR\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0030172\", \"bankAccountNo\": \"20303018563\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(741,10,'RECL391','Bhineswari','Dhruv','','2024-12-03','ranudhruw98@gmail.com','RECL','MTS',60,0,0,'9205',176196.00,'{\"101\": 13325, \"102\": 0, \"103\": 0, \"104\": 0, \"105\": 0, \"106\": 0, \"107\": 0, \"108\": 0, \"109\": 1599, \"GROSS\": 14683}','{\"tds\": 0, \"city\": \"Raipur\", \"state\": \"Chhattisgarh\", \"mobile\": \"8305706245\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Raipur\", \"excelRow\": 35, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"NA\", \"esicNumber\": \"NA\", \"esicEmployee\": 99.94, \"aadhaarNumber\": \"920245429590\", \"rawDesignation\": \"MTS\", \"sourceLocation\": \"RAIPUR\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"HDFC0003416\", \"bankAccountNo\": \"50100755706209\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(742,10,'RECL227','Vivek','Banchhor','','2024-07-15','vivekbanchhor007@gmail.com','RECL','Project Engineer',60,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Raipur\", \"state\": \"Chhattisgarh\", \"mobile\": \"8109698310\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Raipur\", \"excelRow\": 36, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"968744065321\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"RAIPUR\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0012147\", \"bankAccountNo\": \"32554438435\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(743,10,'RECL228','BHUVANESHWAR','PATEL','','2024-07-15','bebo3223@gmail.com','RECL','Project Engineer',60,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Raipur\", \"state\": \"Chhattisgarh\", \"mobile\": \"9098795235\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Raipur\", \"excelRow\": 37, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"373812900006\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"RAIPUR\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0009750\", \"bankAccountNo\": \"35726673257\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(744,10,'RECL131','Shubham','Kumar','','2024-03-22',NULL,'RECL','Junior Accountant',61,0,0,'9203',576000.00,'{\"101\": 24000, \"102\": 9600, \"103\": 1000, \"104\": 1999, \"105\": 1250, \"106\": 10151, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 48000}','{\"tds\": 0, \"city\": \"Ranchi\", \"state\": \"Jharkhand\", \"mobile\": \"\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Ranchi\", \"excelRow\": 38, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Junior Accountant.\", \"sourceLocation\": \"Ranchi\", \"ptLwfWorkmenComp\": 150}','{\"ifscCode\": \"BKID0004997\", \"bankAccountNo\": \"499710110009959\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(745,10,'RECL231','VARUN','KUMAR','','2024-07-15','varunbittu877@gmail.com','RECL','Project Engineer',61,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Ranchi\", \"state\": \"Jharkhand\", \"mobile\": \"7739769734\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Ranchi\", \"excelRow\": 39, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"924780097009\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"Ranchi\", \"ptLwfWorkmenComp\": 150}','{\"ifscCode\": \"SBIN0014827\", \"bankAccountNo\": \"20440725175\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(746,10,'REC150','Savita',NULL,'','2025-04-08','savita10945@gmail.com','RECL','Executive Assistant',63,0,0,'9203',504000.00,'{\"101\": 21000, \"102\": 8400, \"103\": 1000, \"104\": 1749, \"105\": 1250, \"106\": 8601, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 42000}','{\"tds\": 0, \"city\": \"Shimla\", \"state\": \"Himachal Pradesh\", \"mobile\": \"8580810945\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Shimla\", \"excelRow\": 40, \"recovery\": 0, \"panNumber\": \"JWJPS1099K\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"417970459941\", \"rawDesignation\": \"Executive Assistant\", \"sourceLocation\": \"Shimla\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0050118\", \"bankAccountNo\": \"39218148932\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(747,10,'REC303','Susheel','Kumar','','2024-07-19','susheel0006eng@gmail.com','RECL','Project Engineer',63,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Shimla\", \"state\": \"Himachal Pradesh\", \"mobile\": \"9805274075\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Shimla\", \"excelRow\": 41, \"recovery\": 0, \"panNumber\": \"GFDPK1228E\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"258729851504\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"Shimla\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"BARB0EXTSIM\", \"bankAccountNo\": \"98360100005160\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(748,10,'RECL125','Deepthi','S','','2024-03-25','deepthiabhilash01@gmail.com','RECL','Executive Assistant',64,0,0,'9203',504000.00,'{\"101\": 21000, \"102\": 8400, \"103\": 1000, \"104\": 1749, \"105\": 1250, \"106\": 8601, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 42000}','{\"tds\": 0, \"city\": \"Thiruvananthapuram\", \"state\": \"Kerala\", \"mobile\": \"9633202294\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Thiruvananthapuram\", \"excelRow\": 42, \"recovery\": 0, \"panNumber\": \"GNEPS8034P\", \"uanNumber\": \"NIL\", \"esicNumber\": \"NA\", \"esicEmployee\": 0, \"aadhaarNumber\": \"914020315657\", \"rawDesignation\": \"Executive Assistant\", \"sourceLocation\": \"Thiruvananthapuram\", \"ptLwfWorkmenComp\": 300}','{\"ifscCode\": \"CNRB0001824\", \"bankAccountNo\": \"1824101014068\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(749,10,'RECL126','Reshma','Babu','','2024-03-25','reshmachinnu@gmail.com','RECL','Junior Accountant',64,0,0,'9203',576000.00,'{\"101\": 24000, \"102\": 9600, \"103\": 1000, \"104\": 1999, \"105\": 1250, \"106\": 10151, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 48000}','{\"tds\": 0, \"city\": \"Thiruvananthapuram\", \"state\": \"Kerala\", \"mobile\": \"8129837740\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Thiruvananthapuram\", \"excelRow\": 43, \"recovery\": 0, \"panNumber\": \"FZTPR1592H\", \"uanNumber\": \"101814084198\", \"esicNumber\": \"NA\", \"esicEmployee\": 0, \"aadhaarNumber\": \"694573207938\", \"rawDesignation\": \"Jr. Accountant\", \"sourceLocation\": \"Thiruvananthapuram\", \"ptLwfWorkmenComp\": 450}','{\"ifscCode\": \"SBIN0070667\", \"bankAccountNo\": \"67228205786\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(750,10,'RECL127','Aarsha','Sylvester','','2024-03-25','2016aarsha@gmail.com','RECL','Junior Accountant',64,0,0,'9203',576000.00,'{\"101\": 24000, \"102\": 9600, \"103\": 1000, \"104\": 1999, \"105\": 1250, \"106\": 10151, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 48000}','{\"tds\": 0, \"city\": \"Thiruvananthapuram\", \"state\": \"Kerala\", \"mobile\": \"7025774162\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Thiruvananthapuram\", \"excelRow\": 44, \"recovery\": 0, \"panNumber\": \"FCNPA5667K\", \"uanNumber\": \"101955961338\", \"esicNumber\": \"NA\", \"esicEmployee\": 0, \"aadhaarNumber\": \"633880733968\", \"rawDesignation\": \"Jr. Accountant\", \"sourceLocation\": \"Thiruvananthapuram\", \"ptLwfWorkmenComp\": 450}','{\"ifscCode\": \"SBIN0070424\", \"bankAccountNo\": \"41452340606\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(751,10,'RECL128','Vineeth','M G Nair','','2024-03-25','vineethmgnair94@gmail.com','RECL','Office Attendant',64,0,0,'9203',312000.00,'{\"101\": 13000, \"102\": 5200, \"103\": 1000, \"104\": 1082.9, \"105\": 1250, \"106\": 4467, \"107\": 0, \"108\": 0, \"109\": 1560, \"GROSS\": 26000}','{\"tds\": 0, \"city\": \"Thiruvananthapuram\", \"state\": \"Kerala\", \"mobile\": \"9544787114\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Thiruvananthapuram\", \"excelRow\": 45, \"recovery\": 0, \"panNumber\": \"BQGPN0076A\", \"uanNumber\": \"101934104603\", \"esicNumber\": \"4709004989\", \"esicEmployee\": 0, \"aadhaarNumber\": \"537283751692\", \"rawDesignation\": \"Office Attendant/Peon\", \"sourceLocation\": \"Thiruvananthapuram\", \"ptLwfWorkmenComp\": 180}','{\"ifscCode\": \"SBIN0010787\", \"bankAccountNo\": \"32929565710\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(752,10,'RECL 234','ARJUN','BABY U S','','2024-07-13','arjunus14@gmail.com','RECL','Project Engineer',64,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Thiruvananthapuram\", \"state\": \"Kerala\", \"mobile\": \"9020625656\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Thiruvananthapuram\", \"excelRow\": 46, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"739234015968\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"Thiruvananthapuram\", \"ptLwfWorkmenComp\": 450}','{\"ifscCode\": \"SBIN0070422\", \"bankAccountNo\": \"67228271680\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(753,10,'RECL 293','RESHMA','RAJU','','2024-07-15','reshmaneeraj5@gmail.com','RECL','Project Engineer',64,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Thiruvananthapuram\", \"state\": \"Kerala\", \"mobile\": \"8281383944\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Thiruvananthapuram\", \"excelRow\": 47, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"871557056182\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"Thiruvananthapuram\", \"ptLwfWorkmenComp\": 450}','{\"ifscCode\": \"SBIN0070018\", \"bankAccountNo\": \"67055368643\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(754,10,'RECL283','TARUN','MANISH','','2024-07-15','tarunmanish89@gmail.com','RECL','Project Engineer',65,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Vadodara\", \"state\": \"Gujarat\", \"mobile\": \"8285252961\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Vadodara\", \"excelRow\": 48, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"543855632209\", \"rawDesignation\": \"PROJECT ENGINEER\", \"sourceLocation\": \"Vadodra\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"SBIN0000212\", \"bankAccountNo\": \"31792992207\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(755,10,'RECL276','Saurabh','jain','','2024-07-15','saurabh1989jain@gmail.com','RECL','Project Engineer IT',65,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Vadodara\", \"state\": \"Gujarat\", \"mobile\": \"8871458438\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Vadodara\", \"excelRow\": 49, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"832289097861\", \"rawDesignation\": \"Project Engineer (IT)\", \"sourceLocation\": \"Vadodra\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"ICIC0001438\", \"bankAccountNo\": \"656801500528\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(757,10,'RECL188','DOKI','DINESH','','2024-07-15','dinesh.doki1992@gmail.com','RECL','Project Engineer IT',66,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Vijayawada\", \"state\": \"Andhra Pradesh\", \"mobile\": \"9490384276\", \"source\": \"RECL DATA.xlsx\", \"district\": \"NTR\", \"excelRow\": 51, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"315256907180\", \"rawDesignation\": \"Project Engineer - IT\", \"sourceLocation\": \"Vijayawada\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"SBIN0016959\", \"bankAccountNo\": \"20212440078\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(758,10,'RECL371','Muttu','Rama Krishna','','2024-09-03','thotarajesh225@gmail.com','RECL','Project Engineer',66,0,0,'9202',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 2000, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Vijayawada\", \"state\": \"Andhra Pradesh\", \"mobile\": \"8248544506\", \"source\": \"RECL DATA.xlsx\", \"district\": \"NTR\", \"excelRow\": 52, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"341961287434\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"Vijayawada\", \"ptLwfWorkmenComp\": 200}','{\"ifscCode\": \"HDFC0009062\", \"bankAccountNo\": \"50100572884244\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(759,10,'RECL 309','Samiksha',NULL,'','2024-07-29','kmsamiksha940@gmail.com','RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"8737010861\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 53, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"IDIB000M720\", \"bankAccountNo\": \"50276068075\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(760,10,'RECL 245','Yogesh','Kumar','','2024-07-15','ykumar1989@gmail.com','RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"9034517060\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 54, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"HDFC0000479\", \"bankAccountNo\": \"50100269185958\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(761,10,'RECL 247','Purshottam','Kumar Maurya','','2024-07-15','purumaur@gmail.com','RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"8933991441\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 55, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0002506\", \"bankAccountNo\": \"37946763770\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(762,10,'RECL 308','Rohan','Veerwal','','2024-07-29','rohan.veerwal@gmail.com','RECL','Project Engineer',62,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"New Delhi\", \"state\": \"Delhi\", \"mobile\": \"7042216233\", \"source\": \"RECL DATA.xlsx\", \"district\": \"New Delhi\", \"excelRow\": 56, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"SCOPE Complex, New Delhi\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0008110\", \"bankAccountNo\": \"42049213331\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(763,10,'RECL 306','Shivam','Tyagi','','2024-07-29','shivamtyagi0881@gmail.com','RECL','Project Engineer',62,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"New Delhi\", \"state\": \"Delhi\", \"mobile\": \"9910800825\", \"source\": \"RECL DATA.xlsx\", \"district\": \"New Delhi\", \"excelRow\": 57, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"SCOPE Complex, New Delhi\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0011469\", \"bankAccountNo\": \"20415803204\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(764,10,'RECL 316','Aryan','Bansal','','2024-07-31','aryanb9966@gmail.com','RECL','Project Engineer',62,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"New Delhi\", \"state\": \"Delhi\", \"mobile\": \"8929990694\", \"source\": \"RECL DATA.xlsx\", \"district\": \"New Delhi\", \"excelRow\": 58, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"SCOPE Complex, New Delhi\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"HDFC0000278\", \"bankAccountNo\": \"50100263928177\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(765,10,'RECL 214','Ketan','Kumar','','2024-07-15','ketan.kumar75@gmail.com','RECL','Project Engineer',62,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"New Delhi\", \"state\": \"Delhi\", \"mobile\": \"7004820951\", \"source\": \"RECL DATA.xlsx\", \"district\": \"New Delhi\", \"excelRow\": 59, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"SCOPE Complex, New Delhi\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"PUNB0196700\", \"bankAccountNo\": \"1967000102620271\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(766,10,'RECL 242','Siddhartha','Gautam','','2024-07-15','sid240789@gmail.com','RECL','Project Engineer',62,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"New Delhi\", \"state\": \"Delhi\", \"mobile\": \"7479787391\", \"source\": \"RECL DATA.xlsx\", \"district\": \"New Delhi\", \"excelRow\": 60, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"SCOPE Complex, New Delhi\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"HSBC0110004\", \"bankAccountNo\": \"053214573006\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(767,10,'RECL 244','Amarsingh','Yadav','','2024-07-15','amaryadav268@gmail.com','RECL','Project Engineer',62,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"New Delhi\", \"state\": \"Delhi\", \"mobile\": \"9882159014\", \"source\": \"RECL DATA.xlsx\", \"district\": \"New Delhi\", \"excelRow\": 61, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"SCOPE Complex, New Delhi\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"UBIN0566543\", \"bankAccountNo\": \"630702010002657\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(768,10,'RECL 317','Abbas','Ali','','2024-08-01','bijnor.abbas@gmail.com','RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"7991621535\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 62, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0000621\", \"bankAccountNo\": \"35556575921\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(769,10,'RECL319','Amit','Kumar Khudania','','2024-08-01',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"9899998253\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 63, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0009111\", \"bankAccountNo\": \"32009572201\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(770,10,'RECL 251','Dheeraj','Pal','','2024-07-15','dheeraj92.2011@gmail.com','RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"8287496690\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 64, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"IDFB0060132\", \"bankAccountNo\": \"10171269712\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(771,10,'RECL 312','Santosh','Kumar Upadahyay','','2024-07-30','advocateskupadhyay@gmail.com','RECL','IT/Data Consultant',56,0,0,'9204',1320000.00,'{\"101\": 55000, \"102\": 22000, \"103\": 4000, \"104\": 4581.5, \"105\": 1250, \"106\": 23168, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 110000}','{\"tds\": 0, \"city\": \"New Delhi\", \"state\": \"Delhi\", \"mobile\": \"9415582398\", \"source\": \"RECL DATA.xlsx\", \"district\": \"New Delhi\", \"excelRow\": 65, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"IT/Data Consultant\", \"sourceLocation\": \"MNRE\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0001868\", \"bankAccountNo\": \"10852336720\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(772,10,'RECL 206','Vikas','Sadan','','2024-07-11','vsadan9@gmail.com','RECL','Project Engineer IT',62,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"New Delhi\", \"state\": \"Delhi\", \"mobile\": \"9911743570\", \"source\": \"RECL DATA.xlsx\", \"district\": \"New Delhi\", \"excelRow\": 66, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer (IT)\", \"sourceLocation\": \"SCOPE Complex, New Delhi\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"ICIC0004180\", \"bankAccountNo\": \"418001503077\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(773,10,'RECL 268','Lalit','Tyagi','','2024-07-15','tyagilalitgi@gmail.com','RECL','Project Engineer IT',56,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"New Delhi\", \"state\": \"Delhi\", \"mobile\": \"8588977843\", \"source\": \"RECL DATA.xlsx\", \"district\": \"New Delhi\", \"excelRow\": 67, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer (IT)\", \"sourceLocation\": \"MNRE\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"BARB0GOVGHA\", \"bankAccountNo\": \"45580100010794\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(774,10,'RECL 328','Priyanshu','Teotia','','2024-08-12',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"7252043705\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 68, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"IDIB000N047\", \"bankAccountNo\": \"6304870217\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(775,10,'RECL 233','Akash','Nair','','2024-08-12',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"8980643661\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 69, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"HDFC0000890\", \"bankAccountNo\": \"8901460002717\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(776,10,'RECL 325','Amit','Biban','','2024-08-09',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"9950253281\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 70, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0031159\", \"bankAccountNo\": \"42931228584\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(777,10,'RECL 326','Ashish','Sain','','2024-08-09',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"7339877730\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 71, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"BARB0TIJARA\", \"bankAccountNo\": \"34590100001169\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(778,10,'RECL 320','Vipin','Kumar','','2024-08-06',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"8744838417\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 72, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0001274\", \"bankAccountNo\": \"00000051113608478\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(779,10,'RECL 310','Neeraj','Kumar','','2024-08-05',NULL,'RECL','IT/Data Consultant',56,0,0,'9204',1320000.00,'{\"101\": 55000, \"102\": 22000, \"103\": 4000, \"104\": 4581.5, \"105\": 1250, \"106\": 23168, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 110000}','{\"tds\": 5200, \"city\": \"New Delhi\", \"state\": \"Delhi\", \"mobile\": \"9582336006\", \"source\": \"RECL DATA.xlsx\", \"district\": \"New Delhi\", \"excelRow\": 73, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"IT/Data Consultant\", \"sourceLocation\": \"MNRE\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"HDFC0004363\", \"bankAccountNo\": \"05891050267809\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(780,10,'RECL 361','Shiva',NULL,'','2024-09-02',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"7895748295\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 74, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"IDFB0022411\", \"bankAccountNo\": \"10199658956\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(781,10,'RECL 368','Vishnu','Kumar Aryan','','2024-09-03',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"8871865751\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 75, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0008435\", \"bankAccountNo\": \"38508000413\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(782,10,'RECL 369','Rohit','Thakur','','2024-09-03',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"9857500740\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 76, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0001070\", \"bankAccountNo\": \"20076014392\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(783,10,'RECL 374','Lakshay','Tyagi','','2024-09-10',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"8285832169\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 77, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"ICIC0000610\", \"bankAccountNo\": \"061001551392\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(784,10,'RECL 377','Khushboo','Kushwaha','','2024-09-17',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"8787255571\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 78, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"PUNB0468600\", \"bankAccountNo\": \"4686001700128014\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(785,10,'RECL378','Sumit','Kumar','','2024-09-17',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"8210341552\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 79, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"UBIN0919977\", \"bankAccountNo\": \"520101254642105\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(786,10,'RECL 382','Yatender','Singh','','2024-09-25',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"7011934003\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 80, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"IDFB0021332\", \"bankAccountNo\": \"10217529489\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(787,10,'RECL 383','Abhishek','Kumar','','2024-09-25',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"7607211994\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 81, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"CBIN0282225\", \"bankAccountNo\": \"3692429687\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(788,10,'RECL 385','Ankur','Kumar','','2024-09-25',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"9927683660\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 82, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"ICIC0001257\", \"bankAccountNo\": \"125701001816\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(789,10,'RECL 386','Abhishek','Sinha','','2024-09-26',NULL,'RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"9304827625\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 83, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0005331\", \"bankAccountNo\": \"32459371465\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(790,10,'RECL 359','Preeti','Srivastav','','2024-09-09',NULL,'RECL','Project Engineer',56,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"New Delhi\", \"state\": \"Delhi\", \"mobile\": \"7042170885\", \"source\": \"RECL DATA.xlsx\", \"district\": \"New Delhi\", \"excelRow\": 84, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"MNRE\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"KKBK0004587\", \"bankAccountNo\": \"6746452264\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(791,10,'RECL332','Joseph','Tensingh','','2024-09-09','tensingh21@gmail.com','RECL','Project Engineer',50,0,0,'9201',660000.00,'{\"101\": 27500, \"102\": 11000, \"103\": 2000, \"104\": 2290, \"105\": 1250, \"106\": 10960, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 55000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 85, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"SBIN0000823\", \"bankAccountNo\": \"20207829241\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(792,10,'RECL 508','Naman','Bhardwaj','','2025-12-15','viratbhardwaj8171@gmail.com','RECL','Project Engineer',50,0,0,'9201',600000.00,'{\"101\": 25000, \"102\": 10000, \"103\": 2000, \"104\": 2082, \"105\": 1250, \"106\": 9668, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 50000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 86, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"UTIB0004294\", \"bankAccountNo\": \"924010073503916\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(793,10,'RECL 504','Namami','Rastogi','','2025-12-12','namamirastogi2133@gmail.com','RECL','Project Engineer',50,0,0,'9201',600000.00,'{\"101\": 25000, \"102\": 10000, \"103\": 2000, \"104\": 2082, \"105\": 1250, \"106\": 9668, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 50000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"8384857143\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 87, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"HDFC0002839\", \"bankAccountNo\": \"50100750265360\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(794,10,'RECL505','Divakar','Tyagi','','2025-12-11','divakartyagiinfo@gmail.com','RECL','Project Engineer',50,0,0,'9201',600000.00,'{\"101\": 25000, \"102\": 10000, \"103\": 2000, \"104\": 2082, \"105\": 1250, \"106\": 9668, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 50000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"7505556382\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 88, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"IDIB000M055\", \"bankAccountNo\": \"7104066486\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(795,10,'RECL512','Prince','Sharma','','2026-03-02',NULL,'RECL','Project Engineer',50,0,0,'9201',600000.00,'{\"101\": 25000, \"102\": 10000, \"103\": 2000, \"104\": 2082, \"105\": 1250, \"106\": 9668, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 50000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 89, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"HDFC0000061\", \"bankAccountNo\": \"50100266722413\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(796,10,'RECL513','Kapil','Narayan Dwivedi','','2026-03-02',NULL,'RECL','Project Engineer',50,0,0,'9201',600000.00,'{\"101\": 25000, \"102\": 10000, \"103\": 2000, \"104\": 2082, \"105\": 1250, \"106\": 9668, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 50000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 90, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"KKBK0005632\", \"bankAccountNo\": \"3546441030\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(797,10,'RECL 515','Indu','Bala','','2026-05-11',NULL,'RECL','Project Engineer',50,0,0,'9204',1320000.00,'{\"101\": 55000, \"102\": 22000, \"103\": 4000, \"104\": 4581.5, \"105\": 1250, \"106\": 23168, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 110000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 91, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Project Engineer\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"CNRB0002900\", \"bankAccountNo\": \"2900101007785\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(798,10,'RECL 313','Amit','Kumar Gautam','','2024-09-10',NULL,'RECL','Home Office, MoP',50,0,0,'9201',462000.00,'{\"101\": 19250, \"102\": 7700, \"103\": 2000, \"104\": 1603, \"105\": 1250, \"106\": 6697, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 38500}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"8383895354\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 92, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Home Office, MoP\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"ICIC0001354\", \"bankAccountNo\": \"135401523203\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(799,10,'RECL 379','Deepak','Kumar','','2024-09-06',NULL,'RECL','Home Office, MoP',50,0,0,'9201',462000.00,'{\"101\": 19250, \"102\": 7700, \"103\": 2000, \"104\": 1603, \"105\": 1250, \"106\": 6697, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 38500}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"8076316422\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 93, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Home Office, MoP\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"ICIC0002122\", \"bankAccountNo\": \"212201503747\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(800,10,'RECL 387','Praveen','Kumar','','2024-10-01',NULL,'RECL','HR',50,0,0,'9201',462000.00,'{\"101\": 19250, \"102\": 7700, \"103\": 2000, \"104\": 1603, \"105\": 1250, \"106\": 6697, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 38500}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"9810986131\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 94, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"HR\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"PUNB0151300\", \"bankAccountNo\": \"1513000102253090\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(801,10,'RECL502','Gulshan',NULL,'','2025-09-01',NULL,'RECL','HR',50,0,0,'9201',420000.00,'{\"101\": 17500, \"102\": 7000, \"103\": 2000, \"104\": 1457, \"105\": 1250, \"106\": 5793, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 35000}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 95, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"HR\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"BARB0BUPGBX\", \"bankAccountNo\": \"75097399809\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(802,10,'RECL514','Gulshan','Kumar','','2026-04-06',NULL,'RECL','Operations Assistant',50,0,0,'9201',330000.00,'{\"101\": 13750, \"102\": 5500, \"103\": 2000, \"104\": 1145, \"105\": 1250, \"106\": 3855, \"107\": 0, \"108\": 0, \"109\": 1650, \"GROSS\": 27500}','{\"tds\": 0, \"city\": \"Gurugram\", \"state\": \"Haryana\", \"mobile\": \"\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Gurugram\", \"excelRow\": 96, \"recovery\": 0, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"\", \"rawDesignation\": \"Operations Assistant\", \"sourceLocation\": \"CO, Gurugram\", \"ptLwfWorkmenComp\": 0}','{\"ifscCode\": \"ICIC0006298\", \"bankAccountNo\": \"629801119842\"}',1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(803,10,'RECL171-BBN','SUBHANKAR','ROUT','','2024-04-01',NULL,'RECL','Junior Accountant',47,0,0,'9203',576000.00,'{\"101\": 24000, \"102\": 9600, \"103\": 1000, \"104\": 1999, \"105\": 1250, \"106\": 10151, \"107\": 0, \"108\": 0, \"109\": 1800, \"GROSS\": 48000}','{\"tds\": 0, \"city\": \"Baddi-Barotiwala-Nalagarh\", \"state\": \"Himachal Pradesh\", \"mobile\": \"6372950299\", \"source\": \"RECL DATA.xlsx\", \"district\": \"Solan\", \"excelRow\": 7, \"panNumber\": \"\", \"uanNumber\": \"\", \"esicNumber\": \"\", \"esicEmployee\": 0, \"aadhaarNumber\": \"758050871042\", \"rawDesignation\": \"Jr. Accontant\", \"sourceLocation\": \"BBN\", \"ptLwfWorkmenComp\": 0, \"duplicateResolution\": \"Seeded as RECL171-BBN because source has duplicate RECL171 at rows 7 and 50\", \"originalEmployeeCode\": \"RECL171\"}','{\"ifscCode\": \"UJVN0003343\", \"bankAccountNo\": \"3343110080051510\"}',1,'2026-06-28 07:50:43','2026-06-28 07:50:43');
/*!40000 ALTER TABLE `employees` ENABLE KEYS */;

--
-- Table structure for table `employeesalarycomponents`
--

DROP TABLE IF EXISTS `employeesalarycomponents`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `employeesalarycomponents` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `EmployeeId` int NOT NULL,
  `ComponentId` varchar(80) NOT NULL,
  `ComponentCode` varchar(80) NOT NULL DEFAULT '',
  `Amount` decimal(18,4) NOT NULL DEFAULT '0.0000',
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_EmployeeSalaryComponents_Employee_Component` (`EmployeeId`,`ComponentId`)
) ENGINE=InnoDB AUTO_INCREMENT=981 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `employeesalarycomponents`
--

/*!40000 ALTER TABLE `employeesalarycomponents` DISABLE KEYS */;
INSERT INTO `employeesalarycomponents` VALUES (1,596,'101','BASIC',28000.0000,'2026-06-28 09:23:38'),(2,596,'102','HRA',14000.0000,'2026-06-28 09:23:38'),(3,596,'103','TEL_ALLOW',22000.0000,'2026-06-28 09:23:38'),(4,596,'104','STAT_BONUS',1800.0000,'2026-06-28 09:23:38'),(5,597,'101','BASIC',24000.0000,'2026-06-28 09:23:38'),(6,597,'102','HRA',12000.0000,'2026-06-28 09:23:38'),(7,597,'103','TEL_ALLOW',18000.0000,'2026-06-28 09:23:38'),(8,597,'104','STAT_BONUS',1800.0000,'2026-06-28 09:23:38'),(9,598,'101','BASIC',32000.0000,'2026-06-28 09:23:38'),(10,598,'102','HRA',16000.0000,'2026-06-28 09:23:38'),(11,598,'103','TEL_ALLOW',27000.0000,'2026-06-28 09:23:38'),(12,598,'104','STAT_BONUS',1800.0000,'2026-06-28 09:23:38'),(13,599,'101','BASIC',26000.0000,'2026-06-28 09:23:38'),(14,599,'102','HRA',13000.0000,'2026-06-28 09:23:38'),(15,599,'103','TEL_ALLOW',20000.0000,'2026-06-28 09:23:38'),(16,599,'104','STAT_BONUS',1800.0000,'2026-06-28 09:23:38'),(17,601,'101','BASIC',25000.0000,'2026-06-28 09:23:38'),(18,601,'102','HRA',12500.0000,'2026-06-28 09:23:38'),(19,601,'103','TEL_ALLOW',19000.0000,'2026-06-28 09:23:38'),(20,601,'104','STAT_BONUS',1800.0000,'2026-06-28 09:23:38'),(21,602,'101','BASIC',28000.0000,'2026-06-28 09:23:38'),(22,602,'102','HRA',14000.0000,'2026-06-28 09:23:38'),(23,602,'103','TEL_ALLOW',22000.0000,'2026-06-28 09:23:38'),(24,602,'104','STAT_BONUS',1800.0000,'2026-06-28 09:23:38'),(25,603,'101','BASIC',27000.0000,'2026-06-28 09:23:38'),(26,603,'102','HRA',13500.0000,'2026-06-28 09:23:38'),(27,603,'103','TEL_ALLOW',21000.0000,'2026-06-28 09:23:38'),(28,603,'104','STAT_BONUS',1800.0000,'2026-06-28 09:23:38'),(29,604,'101','BASIC',29000.0000,'2026-06-28 09:23:38'),(30,604,'102','HRA',14500.0000,'2026-06-28 09:23:38'),(31,604,'103','TEL_ALLOW',23000.0000,'2026-06-28 09:23:38'),(32,604,'104','STAT_BONUS',1800.0000,'2026-06-28 09:23:38'),(33,615,'101','BASIC',30000.0000,'2026-06-28 09:23:38'),(34,615,'102','HRA',15000.0000,'2026-06-28 09:23:38'),(35,615,'103','TEL_ALLOW',25000.0000,'2026-06-28 09:23:38'),(36,615,'104','STAT_BONUS',1800.0000,'2026-06-28 09:23:38'),(37,616,'101','BASIC',30000.0000,'2026-06-28 09:23:38'),(38,616,'102','HRA',15000.0000,'2026-06-28 09:23:38'),(39,616,'103','TEL_ALLOW',25000.0000,'2026-06-28 09:23:38'),(40,616,'104','STAT_BONUS',1800.0000,'2026-06-28 09:23:38'),(41,709,'101','BASIC',27500.0000,'2026-06-28 09:23:38'),(42,709,'102','HRA',11000.0000,'2026-06-28 09:23:38'),(43,709,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:38'),(44,709,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:38'),(45,709,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:38'),(46,709,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:38'),(47,709,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:38'),(48,709,'108','TA_DA',0.0000,'2026-06-28 09:23:38'),(49,709,'109','PF',1800.0000,'2026-06-28 09:23:38'),(50,709,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:38'),(51,710,'101','BASIC',27500.0000,'2026-06-28 09:23:38'),(52,710,'102','HRA',11000.0000,'2026-06-28 09:23:38'),(53,710,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:38'),(54,710,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:38'),(55,710,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:38'),(56,710,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:38'),(57,710,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:38'),(58,710,'108','TA_DA',0.0000,'2026-06-28 09:23:38'),(59,710,'109','PF',1800.0000,'2026-06-28 09:23:38'),(60,710,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:38'),(61,711,'101','BASIC',27500.0000,'2026-06-28 09:23:38'),(62,711,'102','HRA',11000.0000,'2026-06-28 09:23:38'),(63,711,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:38'),(64,711,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:38'),(65,711,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:38'),(66,711,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:38'),(67,711,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:38'),(68,711,'108','TA_DA',0.0000,'2026-06-28 09:23:38'),(69,711,'109','PF',1800.0000,'2026-06-28 09:23:38'),(70,711,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:38'),(71,712,'101','BASIC',25000.0000,'2026-06-28 09:23:38'),(72,712,'102','HRA',10000.0000,'2026-06-28 09:23:38'),(73,712,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:38'),(74,712,'104','STAT_BONUS',2082.0000,'2026-06-28 09:23:38'),(75,712,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:38'),(76,712,'106','OTHER_ALLOW',9668.0000,'2026-06-28 09:23:38'),(77,712,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:38'),(78,712,'108','TA_DA',0.0000,'2026-06-28 09:23:38'),(79,712,'109','PF',1800.0000,'2026-06-28 09:23:38'),(80,712,'GROSS','GROSS',50000.0000,'2026-06-28 09:23:38'),(81,713,'101','BASIC',22750.0000,'2026-06-28 09:23:38'),(82,713,'102','HRA',9100.0000,'2026-06-28 09:23:39'),(83,713,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:39'),(84,713,'104','STAT_BONUS',1895.0000,'2026-06-28 09:23:39'),(85,713,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:39'),(86,713,'106','OTHER_ALLOW',9505.0000,'2026-06-28 09:23:39'),(87,713,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:39'),(88,713,'108','TA_DA',0.0000,'2026-06-28 09:23:39'),(89,713,'109','PF',1800.0000,'2026-06-28 09:23:39'),(90,713,'GROSS','GROSS',45500.0000,'2026-06-28 09:23:39'),(91,714,'101','BASIC',13000.0000,'2026-06-28 09:23:39'),(92,714,'102','HRA',5200.0000,'2026-06-28 09:23:39'),(93,714,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:39'),(94,714,'104','STAT_BONUS',1082.9000,'2026-06-28 09:23:39'),(95,714,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:39'),(96,714,'106','OTHER_ALLOW',4467.0000,'2026-06-28 09:23:39'),(97,714,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:39'),(98,714,'108','TA_DA',0.0000,'2026-06-28 09:23:39'),(99,714,'109','PF',1560.0000,'2026-06-28 09:23:39'),(100,714,'GROSS','GROSS',26000.0000,'2026-06-28 09:23:39'),(101,715,'101','BASIC',27500.0000,'2026-06-28 09:23:39'),(102,715,'102','HRA',11000.0000,'2026-06-28 09:23:39'),(103,715,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:39'),(104,715,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:39'),(105,715,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:39'),(106,715,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:39'),(107,715,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:39'),(108,715,'108','TA_DA',0.0000,'2026-06-28 09:23:39'),(109,715,'109','PF',1800.0000,'2026-06-28 09:23:39'),(110,715,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:39'),(111,716,'101','BASIC',25000.0000,'2026-06-28 09:23:39'),(112,716,'102','HRA',10000.0000,'2026-06-28 09:23:39'),(113,716,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:39'),(114,716,'104','STAT_BONUS',2082.0000,'2026-06-28 09:23:39'),(115,716,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:39'),(116,716,'106','OTHER_ALLOW',9668.0000,'2026-06-28 09:23:39'),(117,716,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:39'),(118,716,'108','TA_DA',0.0000,'2026-06-28 09:23:39'),(119,716,'109','PF',1800.0000,'2026-06-28 09:23:39'),(120,716,'GROSS','GROSS',50000.0000,'2026-06-28 09:23:39'),(121,717,'101','BASIC',13000.0000,'2026-06-28 09:23:39'),(122,717,'102','HRA',5200.0000,'2026-06-28 09:23:39'),(123,717,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:39'),(124,717,'104','STAT_BONUS',1082.9000,'2026-06-28 09:23:39'),(125,717,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:39'),(126,717,'106','OTHER_ALLOW',4467.0000,'2026-06-28 09:23:39'),(127,717,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:39'),(128,717,'108','TA_DA',0.0000,'2026-06-28 09:23:39'),(129,717,'109','PF',1560.0000,'2026-06-28 09:23:39'),(130,717,'GROSS','GROSS',26000.0000,'2026-06-28 09:23:39'),(131,718,'101','BASIC',13000.0000,'2026-06-28 09:23:39'),(132,718,'102','HRA',5200.0000,'2026-06-28 09:23:39'),(133,718,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:39'),(134,718,'104','STAT_BONUS',1082.9000,'2026-06-28 09:23:39'),(135,718,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:39'),(136,718,'106','OTHER_ALLOW',4467.0000,'2026-06-28 09:23:39'),(137,718,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:39'),(138,718,'108','TA_DA',0.0000,'2026-06-28 09:23:39'),(139,718,'109','PF',1560.0000,'2026-06-28 09:23:39'),(140,718,'GROSS','GROSS',26000.0000,'2026-06-28 09:23:39'),(141,719,'101','BASIC',13000.0000,'2026-06-28 09:23:39'),(142,719,'102','HRA',5200.0000,'2026-06-28 09:23:39'),(143,719,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:39'),(144,719,'104','STAT_BONUS',1082.9000,'2026-06-28 09:23:39'),(145,719,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:39'),(146,719,'106','OTHER_ALLOW',4467.0000,'2026-06-28 09:23:39'),(147,719,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:39'),(148,719,'108','TA_DA',0.0000,'2026-06-28 09:23:39'),(149,719,'109','PF',1560.0000,'2026-06-28 09:23:39'),(150,719,'GROSS','GROSS',26000.0000,'2026-06-28 09:23:39'),(151,720,'101','BASIC',13000.0000,'2026-06-28 09:23:39'),(152,720,'102','HRA',5200.0000,'2026-06-28 09:23:39'),(153,720,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:39'),(154,720,'104','STAT_BONUS',1082.9000,'2026-06-28 09:23:39'),(155,720,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:39'),(156,720,'106','OTHER_ALLOW',4467.0000,'2026-06-28 09:23:39'),(157,720,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:39'),(158,720,'108','TA_DA',0.0000,'2026-06-28 09:23:39'),(159,720,'109','PF',1560.0000,'2026-06-28 09:23:39'),(160,720,'GROSS','GROSS',26000.0000,'2026-06-28 09:23:39'),(161,721,'101','BASIC',27500.0000,'2026-06-28 09:23:39'),(162,721,'102','HRA',11000.0000,'2026-06-28 09:23:39'),(163,721,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:39'),(164,721,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:39'),(165,721,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:39'),(166,721,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:39'),(167,721,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:39'),(168,721,'108','TA_DA',0.0000,'2026-06-28 09:23:39'),(169,721,'109','PF',1800.0000,'2026-06-28 09:23:39'),(170,721,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:39'),(171,722,'101','BASIC',27500.0000,'2026-06-28 09:23:39'),(172,722,'102','HRA',11000.0000,'2026-06-28 09:23:39'),(173,722,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:39'),(174,722,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:39'),(175,722,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:39'),(176,722,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:39'),(177,722,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:39'),(178,722,'108','TA_DA',0.0000,'2026-06-28 09:23:39'),(179,722,'109','PF',1800.0000,'2026-06-28 09:23:39'),(180,722,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:39'),(181,723,'101','BASIC',27500.0000,'2026-06-28 09:23:39'),(182,723,'102','HRA',11000.0000,'2026-06-28 09:23:39'),(183,723,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:39'),(184,723,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:39'),(185,723,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:39'),(186,723,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:39'),(187,723,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:39'),(188,723,'108','TA_DA',0.0000,'2026-06-28 09:23:39'),(189,723,'109','PF',1800.0000,'2026-06-28 09:23:39'),(190,723,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:39'),(191,724,'101','BASIC',13000.0000,'2026-06-28 09:23:39'),(192,724,'102','HRA',5200.0000,'2026-06-28 09:23:39'),(193,724,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:39'),(194,724,'104','STAT_BONUS',1082.9000,'2026-06-28 09:23:39'),(195,724,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:39'),(196,724,'106','OTHER_ALLOW',4467.0000,'2026-06-28 09:23:39'),(197,724,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:39'),(198,724,'108','TA_DA',0.0000,'2026-06-28 09:23:39'),(199,724,'109','PF',1560.0000,'2026-06-28 09:23:39'),(200,724,'GROSS','GROSS',26000.0000,'2026-06-28 09:23:39'),(201,725,'101','BASIC',13000.0000,'2026-06-28 09:23:39'),(202,725,'102','HRA',5200.0000,'2026-06-28 09:23:39'),(203,725,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:39'),(204,725,'104','STAT_BONUS',1082.9000,'2026-06-28 09:23:39'),(205,725,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:39'),(206,725,'106','OTHER_ALLOW',4467.0000,'2026-06-28 09:23:39'),(207,725,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:39'),(208,725,'108','TA_DA',0.0000,'2026-06-28 09:23:39'),(209,725,'109','PF',1560.0000,'2026-06-28 09:23:39'),(210,725,'GROSS','GROSS',26000.0000,'2026-06-28 09:23:39'),(211,726,'101','BASIC',21000.0000,'2026-06-28 09:23:39'),(212,726,'102','HRA',8400.0000,'2026-06-28 09:23:40'),(213,726,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:40'),(214,726,'104','STAT_BONUS',1749.0000,'2026-06-28 09:23:40'),(215,726,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:40'),(216,726,'106','OTHER_ALLOW',8601.0000,'2026-06-28 09:23:40'),(217,726,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:40'),(218,726,'108','TA_DA',0.0000,'2026-06-28 09:23:40'),(219,726,'109','PF',1800.0000,'2026-06-28 09:23:40'),(220,726,'GROSS','GROSS',42000.0000,'2026-06-28 09:23:40'),(221,727,'101','BASIC',21000.0000,'2026-06-28 09:23:40'),(222,727,'102','HRA',8400.0000,'2026-06-28 09:23:40'),(223,727,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:40'),(224,727,'104','STAT_BONUS',1749.0000,'2026-06-28 09:23:40'),(225,727,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:40'),(226,727,'106','OTHER_ALLOW',8601.0000,'2026-06-28 09:23:40'),(227,727,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:40'),(228,727,'108','TA_DA',0.0000,'2026-06-28 09:23:40'),(229,727,'109','PF',1800.0000,'2026-06-28 09:23:40'),(230,727,'GROSS','GROSS',42000.0000,'2026-06-28 09:23:40'),(231,728,'101','BASIC',24000.0000,'2026-06-28 09:23:40'),(232,728,'102','HRA',9600.0000,'2026-06-28 09:23:40'),(233,728,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:40'),(234,728,'104','STAT_BONUS',1999.0000,'2026-06-28 09:23:40'),(235,728,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:40'),(236,728,'106','OTHER_ALLOW',10151.0000,'2026-06-28 09:23:40'),(237,728,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:40'),(238,728,'108','TA_DA',0.0000,'2026-06-28 09:23:40'),(239,728,'109','PF',1800.0000,'2026-06-28 09:23:40'),(240,728,'GROSS','GROSS',48000.0000,'2026-06-28 09:23:40'),(241,729,'101','BASIC',13000.0000,'2026-06-28 09:23:40'),(242,729,'102','HRA',5200.0000,'2026-06-28 09:23:40'),(243,729,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:40'),(244,729,'104','STAT_BONUS',1082.9000,'2026-06-28 09:23:40'),(245,729,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:40'),(246,729,'106','OTHER_ALLOW',4467.0000,'2026-06-28 09:23:40'),(247,729,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:40'),(248,729,'108','TA_DA',0.0000,'2026-06-28 09:23:40'),(249,729,'109','PF',1560.0000,'2026-06-28 09:23:40'),(250,729,'GROSS','GROSS',26000.0000,'2026-06-28 09:23:40'),(251,730,'101','BASIC',30000.0000,'2026-06-28 09:23:40'),(252,730,'102','HRA',12000.0000,'2026-06-28 09:23:40'),(253,730,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:40'),(254,730,'104','STAT_BONUS',2499.0000,'2026-06-28 09:23:40'),(255,730,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:40'),(256,730,'106','OTHER_ALLOW',12251.0000,'2026-06-28 09:23:40'),(257,730,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:40'),(258,730,'108','TA_DA',0.0000,'2026-06-28 09:23:40'),(259,730,'109','PF',1800.0000,'2026-06-28 09:23:40'),(260,730,'GROSS','GROSS',60000.0000,'2026-06-28 09:23:40'),(261,731,'101','BASIC',27500.0000,'2026-06-28 09:23:40'),(262,731,'102','HRA',11000.0000,'2026-06-28 09:23:40'),(263,731,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:40'),(264,731,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:40'),(265,731,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:40'),(266,731,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:40'),(267,731,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:40'),(268,731,'108','TA_DA',0.0000,'2026-06-28 09:23:40'),(269,731,'109','PF',1800.0000,'2026-06-28 09:23:40'),(270,731,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:40'),(271,732,'101','BASIC',13000.0000,'2026-06-28 09:23:40'),(272,732,'102','HRA',5200.0000,'2026-06-28 09:23:40'),(273,732,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:40'),(274,732,'104','STAT_BONUS',1082.9000,'2026-06-28 09:23:40'),(275,732,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:40'),(276,732,'106','OTHER_ALLOW',4467.0000,'2026-06-28 09:23:40'),(277,732,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:40'),(278,732,'108','TA_DA',0.0000,'2026-06-28 09:23:40'),(279,732,'109','PF',1560.0000,'2026-06-28 09:23:40'),(280,732,'GROSS','GROSS',26000.0000,'2026-06-28 09:23:40'),(281,733,'101','BASIC',12000.0000,'2026-06-28 09:23:40'),(282,733,'102','HRA',4800.0000,'2026-06-28 09:23:40'),(283,733,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:40'),(284,733,'104','STAT_BONUS',999.6000,'2026-06-28 09:23:40'),(285,733,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:40'),(286,733,'106','OTHER_ALLOW',3950.0000,'2026-06-28 09:23:40'),(287,733,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:40'),(288,733,'108','TA_DA',0.0000,'2026-06-28 09:23:40'),(289,733,'109','PF',1440.0000,'2026-06-28 09:23:40'),(290,733,'GROSS','GROSS',24000.0000,'2026-06-28 09:23:40'),(291,734,'101','BASIC',27500.0000,'2026-06-28 09:23:40'),(292,734,'102','HRA',11000.0000,'2026-06-28 09:23:40'),(293,734,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:40'),(294,734,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:40'),(295,734,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:40'),(296,734,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:40'),(297,734,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:40'),(298,734,'108','TA_DA',0.0000,'2026-06-28 09:23:40'),(299,734,'109','PF',1800.0000,'2026-06-28 09:23:40'),(300,734,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:40'),(301,735,'101','BASIC',21000.0000,'2026-06-28 09:23:40'),(302,735,'102','HRA',8400.0000,'2026-06-28 09:23:40'),(303,735,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:40'),(304,735,'104','STAT_BONUS',1749.0000,'2026-06-28 09:23:40'),(305,735,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:40'),(306,735,'106','OTHER_ALLOW',8601.0000,'2026-06-28 09:23:40'),(307,735,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:40'),(308,735,'108','TA_DA',0.0000,'2026-06-28 09:23:40'),(309,735,'109','PF',1800.0000,'2026-06-28 09:23:40'),(310,735,'GROSS','GROSS',42000.0000,'2026-06-28 09:23:40'),(311,736,'101','BASIC',16250.0000,'2026-06-28 09:23:40'),(312,736,'102','HRA',6500.0000,'2026-06-28 09:23:40'),(313,736,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:40'),(314,736,'104','STAT_BONUS',1353.0000,'2026-06-28 09:23:40'),(315,736,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:40'),(316,736,'106','OTHER_ALLOW',6147.0000,'2026-06-28 09:23:40'),(317,736,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:40'),(318,736,'108','TA_DA',0.0000,'2026-06-28 09:23:40'),(319,736,'109','PF',1800.0000,'2026-06-28 09:23:40'),(320,736,'GROSS','GROSS',32500.0000,'2026-06-28 09:23:40'),(321,737,'101','BASIC',13000.0000,'2026-06-28 09:23:40'),(322,737,'102','HRA',5200.0000,'2026-06-28 09:23:40'),(323,737,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:40'),(324,737,'104','STAT_BONUS',1082.9000,'2026-06-28 09:23:40'),(325,737,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:40'),(326,737,'106','OTHER_ALLOW',4467.0000,'2026-06-28 09:23:40'),(327,737,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:40'),(328,737,'108','TA_DA',0.0000,'2026-06-28 09:23:40'),(329,737,'109','PF',1560.0000,'2026-06-28 09:23:40'),(330,737,'GROSS','GROSS',26000.0000,'2026-06-28 09:23:40'),(331,738,'101','BASIC',12000.0000,'2026-06-28 09:23:40'),(332,738,'102','HRA',4800.0000,'2026-06-28 09:23:40'),(333,738,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:40'),(334,738,'104','STAT_BONUS',999.6000,'2026-06-28 09:23:40'),(335,738,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:40'),(336,738,'106','OTHER_ALLOW',3950.0000,'2026-06-28 09:23:40'),(337,738,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:40'),(338,738,'108','TA_DA',0.0000,'2026-06-28 09:23:40'),(339,738,'109','PF',1440.0000,'2026-06-28 09:23:40'),(340,738,'GROSS','GROSS',24000.0000,'2026-06-28 09:23:40'),(341,739,'101','BASIC',27500.0000,'2026-06-28 09:23:40'),(342,739,'102','HRA',11000.0000,'2026-06-28 09:23:40'),(343,739,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:40'),(344,739,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:40'),(345,739,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:40'),(346,739,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:41'),(347,739,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:41'),(348,739,'108','TA_DA',0.0000,'2026-06-28 09:23:41'),(349,739,'109','PF',1800.0000,'2026-06-28 09:23:41'),(350,739,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:41'),(351,740,'101','BASIC',27500.0000,'2026-06-28 09:23:41'),(352,740,'102','HRA',11000.0000,'2026-06-28 09:23:41'),(353,740,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:41'),(354,740,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:41'),(355,740,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:41'),(356,740,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:41'),(357,740,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:41'),(358,740,'108','TA_DA',0.0000,'2026-06-28 09:23:41'),(359,740,'109','PF',1800.0000,'2026-06-28 09:23:41'),(360,740,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:41'),(361,741,'101','BASIC',13325.0000,'2026-06-28 09:23:41'),(362,741,'102','HRA',0.0000,'2026-06-28 09:23:41'),(363,741,'103','TEL_ALLOW',0.0000,'2026-06-28 09:23:41'),(364,741,'104','STAT_BONUS',0.0000,'2026-06-28 09:23:41'),(365,741,'105','MED_ALLOW',0.0000,'2026-06-28 09:23:41'),(366,741,'106','OTHER_ALLOW',0.0000,'2026-06-28 09:23:41'),(367,741,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:41'),(368,741,'108','TA_DA',0.0000,'2026-06-28 09:23:41'),(369,741,'109','PF',1599.0000,'2026-06-28 09:23:41'),(370,741,'GROSS','GROSS',14683.0000,'2026-06-28 09:23:41'),(371,742,'101','BASIC',27500.0000,'2026-06-28 09:23:41'),(372,742,'102','HRA',11000.0000,'2026-06-28 09:23:41'),(373,742,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:41'),(374,742,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:41'),(375,742,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:41'),(376,742,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:41'),(377,742,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:41'),(378,742,'108','TA_DA',0.0000,'2026-06-28 09:23:41'),(379,742,'109','PF',1800.0000,'2026-06-28 09:23:41'),(380,742,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:41'),(381,743,'101','BASIC',27500.0000,'2026-06-28 09:23:41'),(382,743,'102','HRA',11000.0000,'2026-06-28 09:23:41'),(383,743,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:41'),(384,743,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:41'),(385,743,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:41'),(386,743,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:41'),(387,743,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:41'),(388,743,'108','TA_DA',0.0000,'2026-06-28 09:23:41'),(389,743,'109','PF',1800.0000,'2026-06-28 09:23:41'),(390,743,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:41'),(391,744,'101','BASIC',24000.0000,'2026-06-28 09:23:41'),(392,744,'102','HRA',9600.0000,'2026-06-28 09:23:41'),(393,744,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:41'),(394,744,'104','STAT_BONUS',1999.0000,'2026-06-28 09:23:41'),(395,744,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:41'),(396,744,'106','OTHER_ALLOW',10151.0000,'2026-06-28 09:23:41'),(397,744,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:41'),(398,744,'108','TA_DA',0.0000,'2026-06-28 09:23:41'),(399,744,'109','PF',1800.0000,'2026-06-28 09:23:41'),(400,744,'GROSS','GROSS',48000.0000,'2026-06-28 09:23:41'),(401,745,'101','BASIC',27500.0000,'2026-06-28 09:23:41'),(402,745,'102','HRA',11000.0000,'2026-06-28 09:23:41'),(403,745,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:41'),(404,745,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:41'),(405,745,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:41'),(406,745,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:41'),(407,745,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:41'),(408,745,'108','TA_DA',0.0000,'2026-06-28 09:23:41'),(409,745,'109','PF',1800.0000,'2026-06-28 09:23:41'),(410,745,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:41'),(411,746,'101','BASIC',21000.0000,'2026-06-28 09:23:41'),(412,746,'102','HRA',8400.0000,'2026-06-28 09:23:41'),(413,746,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:41'),(414,746,'104','STAT_BONUS',1749.0000,'2026-06-28 09:23:41'),(415,746,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:41'),(416,746,'106','OTHER_ALLOW',8601.0000,'2026-06-28 09:23:41'),(417,746,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:41'),(418,746,'108','TA_DA',0.0000,'2026-06-28 09:23:41'),(419,746,'109','PF',1800.0000,'2026-06-28 09:23:41'),(420,746,'GROSS','GROSS',42000.0000,'2026-06-28 09:23:41'),(421,747,'101','BASIC',27500.0000,'2026-06-28 09:23:41'),(422,747,'102','HRA',11000.0000,'2026-06-28 09:23:41'),(423,747,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:41'),(424,747,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:41'),(425,747,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:41'),(426,747,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:41'),(427,747,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:41'),(428,747,'108','TA_DA',0.0000,'2026-06-28 09:23:41'),(429,747,'109','PF',1800.0000,'2026-06-28 09:23:41'),(430,747,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:41'),(431,748,'101','BASIC',21000.0000,'2026-06-28 09:23:41'),(432,748,'102','HRA',8400.0000,'2026-06-28 09:23:41'),(433,748,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:41'),(434,748,'104','STAT_BONUS',1749.0000,'2026-06-28 09:23:41'),(435,748,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:41'),(436,748,'106','OTHER_ALLOW',8601.0000,'2026-06-28 09:23:41'),(437,748,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:41'),(438,748,'108','TA_DA',0.0000,'2026-06-28 09:23:41'),(439,748,'109','PF',1800.0000,'2026-06-28 09:23:41'),(440,748,'GROSS','GROSS',42000.0000,'2026-06-28 09:23:41'),(441,749,'101','BASIC',24000.0000,'2026-06-28 09:23:41'),(442,749,'102','HRA',9600.0000,'2026-06-28 09:23:41'),(443,749,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:41'),(444,749,'104','STAT_BONUS',1999.0000,'2026-06-28 09:23:41'),(445,749,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:41'),(446,749,'106','OTHER_ALLOW',10151.0000,'2026-06-28 09:23:41'),(447,749,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:41'),(448,749,'108','TA_DA',0.0000,'2026-06-28 09:23:41'),(449,749,'109','PF',1800.0000,'2026-06-28 09:23:41'),(450,749,'GROSS','GROSS',48000.0000,'2026-06-28 09:23:41'),(451,750,'101','BASIC',24000.0000,'2026-06-28 09:23:41'),(452,750,'102','HRA',9600.0000,'2026-06-28 09:23:41'),(453,750,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:41'),(454,750,'104','STAT_BONUS',1999.0000,'2026-06-28 09:23:41'),(455,750,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:41'),(456,750,'106','OTHER_ALLOW',10151.0000,'2026-06-28 09:23:41'),(457,750,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:41'),(458,750,'108','TA_DA',0.0000,'2026-06-28 09:23:42'),(459,750,'109','PF',1800.0000,'2026-06-28 09:23:42'),(460,750,'GROSS','GROSS',48000.0000,'2026-06-28 09:23:42'),(461,751,'101','BASIC',13000.0000,'2026-06-28 09:23:42'),(462,751,'102','HRA',5200.0000,'2026-06-28 09:23:42'),(463,751,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:42'),(464,751,'104','STAT_BONUS',1082.9000,'2026-06-28 09:23:42'),(465,751,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:42'),(466,751,'106','OTHER_ALLOW',4467.0000,'2026-06-28 09:23:42'),(467,751,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:42'),(468,751,'108','TA_DA',0.0000,'2026-06-28 09:23:42'),(469,751,'109','PF',1560.0000,'2026-06-28 09:23:42'),(470,751,'GROSS','GROSS',26000.0000,'2026-06-28 09:23:42'),(471,752,'101','BASIC',27500.0000,'2026-06-28 09:23:42'),(472,752,'102','HRA',11000.0000,'2026-06-28 09:23:42'),(473,752,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:42'),(474,752,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:42'),(475,752,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:42'),(476,752,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:42'),(477,752,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:42'),(478,752,'108','TA_DA',0.0000,'2026-06-28 09:23:42'),(479,752,'109','PF',1800.0000,'2026-06-28 09:23:42'),(480,752,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:42'),(481,753,'101','BASIC',27500.0000,'2026-06-28 09:23:42'),(482,753,'102','HRA',11000.0000,'2026-06-28 09:23:42'),(483,753,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:42'),(484,753,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:42'),(485,753,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:42'),(486,753,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:42'),(487,753,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:42'),(488,753,'108','TA_DA',0.0000,'2026-06-28 09:23:42'),(489,753,'109','PF',1800.0000,'2026-06-28 09:23:42'),(490,753,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:42'),(491,754,'101','BASIC',27500.0000,'2026-06-28 09:23:42'),(492,754,'102','HRA',11000.0000,'2026-06-28 09:23:42'),(493,754,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:42'),(494,754,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:42'),(495,754,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:42'),(496,754,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:42'),(497,754,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:42'),(498,754,'108','TA_DA',0.0000,'2026-06-28 09:23:42'),(499,754,'109','PF',1800.0000,'2026-06-28 09:23:42'),(500,754,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:42'),(501,755,'101','BASIC',27500.0000,'2026-06-28 09:23:42'),(502,755,'102','HRA',11000.0000,'2026-06-28 09:23:42'),(503,755,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:42'),(504,755,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:42'),(505,755,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:42'),(506,755,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:42'),(507,755,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:42'),(508,755,'108','TA_DA',0.0000,'2026-06-28 09:23:42'),(509,755,'109','PF',1800.0000,'2026-06-28 09:23:42'),(510,755,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:42'),(511,757,'101','BASIC',27500.0000,'2026-06-28 09:23:42'),(512,757,'102','HRA',11000.0000,'2026-06-28 09:23:42'),(513,757,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:42'),(514,757,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:42'),(515,757,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:42'),(516,757,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:42'),(517,757,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:42'),(518,757,'108','TA_DA',0.0000,'2026-06-28 09:23:42'),(519,757,'109','PF',1800.0000,'2026-06-28 09:23:42'),(520,757,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:42'),(521,758,'101','BASIC',27500.0000,'2026-06-28 09:23:42'),(522,758,'102','HRA',11000.0000,'2026-06-28 09:23:42'),(523,758,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:42'),(524,758,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:42'),(525,758,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:42'),(526,758,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:42'),(527,758,'107','LAPTOP_ALLOW',2000.0000,'2026-06-28 09:23:42'),(528,758,'108','TA_DA',0.0000,'2026-06-28 09:23:42'),(529,758,'109','PF',1800.0000,'2026-06-28 09:23:42'),(530,758,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:42'),(531,759,'101','BASIC',27500.0000,'2026-06-28 09:23:42'),(532,759,'102','HRA',11000.0000,'2026-06-28 09:23:42'),(533,759,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:42'),(534,759,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:42'),(535,759,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:42'),(536,759,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:42'),(537,759,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:42'),(538,759,'108','TA_DA',0.0000,'2026-06-28 09:23:42'),(539,759,'109','PF',1800.0000,'2026-06-28 09:23:42'),(540,759,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:42'),(541,760,'101','BASIC',27500.0000,'2026-06-28 09:23:42'),(542,760,'102','HRA',11000.0000,'2026-06-28 09:23:42'),(543,760,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:42'),(544,760,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:42'),(545,760,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:42'),(546,760,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:42'),(547,760,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:42'),(548,760,'108','TA_DA',0.0000,'2026-06-28 09:23:42'),(549,760,'109','PF',1800.0000,'2026-06-28 09:23:42'),(550,760,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:42'),(551,761,'101','BASIC',27500.0000,'2026-06-28 09:23:42'),(552,761,'102','HRA',11000.0000,'2026-06-28 09:23:42'),(553,761,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:42'),(554,761,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:42'),(555,761,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:42'),(556,761,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:42'),(557,761,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:42'),(558,761,'108','TA_DA',0.0000,'2026-06-28 09:23:42'),(559,761,'109','PF',1800.0000,'2026-06-28 09:23:42'),(560,761,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:42'),(561,762,'101','BASIC',27500.0000,'2026-06-28 09:23:42'),(562,762,'102','HRA',11000.0000,'2026-06-28 09:23:42'),(563,762,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:42'),(564,762,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:42'),(565,762,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:43'),(566,762,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:43'),(567,762,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:43'),(568,762,'108','TA_DA',0.0000,'2026-06-28 09:23:43'),(569,762,'109','PF',1800.0000,'2026-06-28 09:23:43'),(570,762,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:43'),(571,763,'101','BASIC',27500.0000,'2026-06-28 09:23:43'),(572,763,'102','HRA',11000.0000,'2026-06-28 09:23:43'),(573,763,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:43'),(574,763,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:43'),(575,763,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:43'),(576,763,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:43'),(577,763,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:43'),(578,763,'108','TA_DA',0.0000,'2026-06-28 09:23:43'),(579,763,'109','PF',1800.0000,'2026-06-28 09:23:43'),(580,763,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:43'),(581,764,'101','BASIC',27500.0000,'2026-06-28 09:23:43'),(582,764,'102','HRA',11000.0000,'2026-06-28 09:23:43'),(583,764,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:43'),(584,764,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:43'),(585,764,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:43'),(586,764,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:43'),(587,764,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:43'),(588,764,'108','TA_DA',0.0000,'2026-06-28 09:23:43'),(589,764,'109','PF',1800.0000,'2026-06-28 09:23:43'),(590,764,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:43'),(591,765,'101','BASIC',27500.0000,'2026-06-28 09:23:43'),(592,765,'102','HRA',11000.0000,'2026-06-28 09:23:43'),(593,765,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:43'),(594,765,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:43'),(595,765,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:43'),(596,765,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:43'),(597,765,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:43'),(598,765,'108','TA_DA',0.0000,'2026-06-28 09:23:43'),(599,765,'109','PF',1800.0000,'2026-06-28 09:23:43'),(600,765,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:43'),(601,766,'101','BASIC',27500.0000,'2026-06-28 09:23:43'),(602,766,'102','HRA',11000.0000,'2026-06-28 09:23:43'),(603,766,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:43'),(604,766,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:43'),(605,766,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:43'),(606,766,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:43'),(607,766,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:43'),(608,766,'108','TA_DA',0.0000,'2026-06-28 09:23:43'),(609,766,'109','PF',1800.0000,'2026-06-28 09:23:43'),(610,766,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:43'),(611,767,'101','BASIC',27500.0000,'2026-06-28 09:23:43'),(612,767,'102','HRA',11000.0000,'2026-06-28 09:23:43'),(613,767,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:43'),(614,767,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:43'),(615,767,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:43'),(616,767,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:43'),(617,767,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:43'),(618,767,'108','TA_DA',0.0000,'2026-06-28 09:23:43'),(619,767,'109','PF',1800.0000,'2026-06-28 09:23:43'),(620,767,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:43'),(621,768,'101','BASIC',27500.0000,'2026-06-28 09:23:43'),(622,768,'102','HRA',11000.0000,'2026-06-28 09:23:43'),(623,768,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:43'),(624,768,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:43'),(625,768,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:43'),(626,768,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:43'),(627,768,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:43'),(628,768,'108','TA_DA',0.0000,'2026-06-28 09:23:43'),(629,768,'109','PF',1800.0000,'2026-06-28 09:23:43'),(630,768,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:43'),(631,769,'101','BASIC',27500.0000,'2026-06-28 09:23:43'),(632,769,'102','HRA',11000.0000,'2026-06-28 09:23:43'),(633,769,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:43'),(634,769,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:43'),(635,769,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:43'),(636,769,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:43'),(637,769,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:43'),(638,769,'108','TA_DA',0.0000,'2026-06-28 09:23:43'),(639,769,'109','PF',1800.0000,'2026-06-28 09:23:43'),(640,769,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:43'),(641,770,'101','BASIC',27500.0000,'2026-06-28 09:23:43'),(642,770,'102','HRA',11000.0000,'2026-06-28 09:23:43'),(643,770,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:43'),(644,770,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:43'),(645,770,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:43'),(646,770,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:43'),(647,770,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:43'),(648,770,'108','TA_DA',0.0000,'2026-06-28 09:23:43'),(649,770,'109','PF',1800.0000,'2026-06-28 09:23:43'),(650,770,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:43'),(651,771,'101','BASIC',55000.0000,'2026-06-28 09:23:43'),(652,771,'102','HRA',22000.0000,'2026-06-28 09:23:43'),(653,771,'103','TEL_ALLOW',4000.0000,'2026-06-28 09:23:43'),(654,771,'104','STAT_BONUS',4581.5000,'2026-06-28 09:23:43'),(655,771,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:43'),(656,771,'106','OTHER_ALLOW',23168.0000,'2026-06-28 09:23:43'),(657,771,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:43'),(658,771,'108','TA_DA',0.0000,'2026-06-28 09:23:43'),(659,771,'109','PF',1800.0000,'2026-06-28 09:23:43'),(660,771,'GROSS','GROSS',110000.0000,'2026-06-28 09:23:43'),(661,772,'101','BASIC',27500.0000,'2026-06-28 09:23:43'),(662,772,'102','HRA',11000.0000,'2026-06-28 09:23:43'),(663,772,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:43'),(664,772,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:43'),(665,772,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:43'),(666,772,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:43'),(667,772,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:43'),(668,772,'108','TA_DA',0.0000,'2026-06-28 09:23:43'),(669,772,'109','PF',1800.0000,'2026-06-28 09:23:43'),(670,772,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:43'),(671,773,'101','BASIC',27500.0000,'2026-06-28 09:23:43'),(672,773,'102','HRA',11000.0000,'2026-06-28 09:23:43'),(673,773,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:43'),(674,773,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:43'),(675,773,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:43'),(676,773,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:43'),(677,773,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:43'),(678,773,'108','TA_DA',0.0000,'2026-06-28 09:23:43'),(679,773,'109','PF',1800.0000,'2026-06-28 09:23:44'),(680,773,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:44'),(681,774,'101','BASIC',27500.0000,'2026-06-28 09:23:44'),(682,774,'102','HRA',11000.0000,'2026-06-28 09:23:44'),(683,774,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:44'),(684,774,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:44'),(685,774,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:44'),(686,774,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:44'),(687,774,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:44'),(688,774,'108','TA_DA',0.0000,'2026-06-28 09:23:44'),(689,774,'109','PF',1800.0000,'2026-06-28 09:23:44'),(690,774,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:44'),(691,775,'101','BASIC',27500.0000,'2026-06-28 09:23:44'),(692,775,'102','HRA',11000.0000,'2026-06-28 09:23:44'),(693,775,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:44'),(694,775,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:44'),(695,775,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:44'),(696,775,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:44'),(697,775,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:44'),(698,775,'108','TA_DA',0.0000,'2026-06-28 09:23:44'),(699,775,'109','PF',1800.0000,'2026-06-28 09:23:44'),(700,775,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:44'),(701,776,'101','BASIC',27500.0000,'2026-06-28 09:23:44'),(702,776,'102','HRA',11000.0000,'2026-06-28 09:23:44'),(703,776,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:44'),(704,776,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:44'),(705,776,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:44'),(706,776,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:44'),(707,776,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:44'),(708,776,'108','TA_DA',0.0000,'2026-06-28 09:23:44'),(709,776,'109','PF',1800.0000,'2026-06-28 09:23:44'),(710,776,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:44'),(711,777,'101','BASIC',27500.0000,'2026-06-28 09:23:44'),(712,777,'102','HRA',11000.0000,'2026-06-28 09:23:44'),(713,777,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:44'),(714,777,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:44'),(715,777,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:44'),(716,777,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:44'),(717,777,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:44'),(718,777,'108','TA_DA',0.0000,'2026-06-28 09:23:44'),(719,777,'109','PF',1800.0000,'2026-06-28 09:23:44'),(720,777,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:44'),(721,778,'101','BASIC',27500.0000,'2026-06-28 09:23:44'),(722,778,'102','HRA',11000.0000,'2026-06-28 09:23:44'),(723,778,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:44'),(724,778,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:44'),(725,778,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:44'),(726,778,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:44'),(727,778,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:44'),(728,778,'108','TA_DA',0.0000,'2026-06-28 09:23:44'),(729,778,'109','PF',1800.0000,'2026-06-28 09:23:44'),(730,778,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:44'),(731,779,'101','BASIC',55000.0000,'2026-06-28 09:23:44'),(732,779,'102','HRA',22000.0000,'2026-06-28 09:23:44'),(733,779,'103','TEL_ALLOW',4000.0000,'2026-06-28 09:23:44'),(734,779,'104','STAT_BONUS',4581.5000,'2026-06-28 09:23:44'),(735,779,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:44'),(736,779,'106','OTHER_ALLOW',23168.0000,'2026-06-28 09:23:44'),(737,779,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:44'),(738,779,'108','TA_DA',0.0000,'2026-06-28 09:23:44'),(739,779,'109','PF',1800.0000,'2026-06-28 09:23:44'),(740,779,'GROSS','GROSS',110000.0000,'2026-06-28 09:23:44'),(741,780,'101','BASIC',27500.0000,'2026-06-28 09:23:44'),(742,780,'102','HRA',11000.0000,'2026-06-28 09:23:44'),(743,780,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:44'),(744,780,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:44'),(745,780,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:44'),(746,780,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:44'),(747,780,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:44'),(748,780,'108','TA_DA',0.0000,'2026-06-28 09:23:44'),(749,780,'109','PF',1800.0000,'2026-06-28 09:23:44'),(750,780,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:44'),(751,781,'101','BASIC',27500.0000,'2026-06-28 09:23:44'),(752,781,'102','HRA',11000.0000,'2026-06-28 09:23:44'),(753,781,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:44'),(754,781,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:44'),(755,781,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:44'),(756,781,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:44'),(757,781,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:44'),(758,781,'108','TA_DA',0.0000,'2026-06-28 09:23:44'),(759,781,'109','PF',1800.0000,'2026-06-28 09:23:44'),(760,781,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:44'),(761,782,'101','BASIC',27500.0000,'2026-06-28 09:23:44'),(762,782,'102','HRA',11000.0000,'2026-06-28 09:23:44'),(763,782,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:44'),(764,782,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:44'),(765,782,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:44'),(766,782,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:44'),(767,782,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:44'),(768,782,'108','TA_DA',0.0000,'2026-06-28 09:23:44'),(769,782,'109','PF',1800.0000,'2026-06-28 09:23:44'),(770,782,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:44'),(771,783,'101','BASIC',27500.0000,'2026-06-28 09:23:44'),(772,783,'102','HRA',11000.0000,'2026-06-28 09:23:44'),(773,783,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:44'),(774,783,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:44'),(775,783,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:45'),(776,783,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:45'),(777,783,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:45'),(778,783,'108','TA_DA',0.0000,'2026-06-28 09:23:45'),(779,783,'109','PF',1800.0000,'2026-06-28 09:23:45'),(780,783,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:45'),(781,784,'101','BASIC',27500.0000,'2026-06-28 09:23:45'),(782,784,'102','HRA',11000.0000,'2026-06-28 09:23:45'),(783,784,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:45'),(784,784,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:45'),(785,784,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:45'),(786,784,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:45'),(787,784,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:45'),(788,784,'108','TA_DA',0.0000,'2026-06-28 09:23:45'),(789,784,'109','PF',1800.0000,'2026-06-28 09:23:45'),(790,784,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:45'),(791,785,'101','BASIC',27500.0000,'2026-06-28 09:23:45'),(792,785,'102','HRA',11000.0000,'2026-06-28 09:23:45'),(793,785,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:45'),(794,785,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:45'),(795,785,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:45'),(796,785,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:45'),(797,785,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:45'),(798,785,'108','TA_DA',0.0000,'2026-06-28 09:23:45'),(799,785,'109','PF',1800.0000,'2026-06-28 09:23:45'),(800,785,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:45'),(801,786,'101','BASIC',27500.0000,'2026-06-28 09:23:45'),(802,786,'102','HRA',11000.0000,'2026-06-28 09:23:45'),(803,786,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:45'),(804,786,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:45'),(805,786,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:45'),(806,786,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:45'),(807,786,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:45'),(808,786,'108','TA_DA',0.0000,'2026-06-28 09:23:45'),(809,786,'109','PF',1800.0000,'2026-06-28 09:23:45'),(810,786,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:45'),(811,787,'101','BASIC',27500.0000,'2026-06-28 09:23:45'),(812,787,'102','HRA',11000.0000,'2026-06-28 09:23:45'),(813,787,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:45'),(814,787,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:45'),(815,787,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:45'),(816,787,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:45'),(817,787,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:45'),(818,787,'108','TA_DA',0.0000,'2026-06-28 09:23:45'),(819,787,'109','PF',1800.0000,'2026-06-28 09:23:45'),(820,787,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:45'),(821,788,'101','BASIC',27500.0000,'2026-06-28 09:23:45'),(822,788,'102','HRA',11000.0000,'2026-06-28 09:23:45'),(823,788,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:45'),(824,788,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:45'),(825,788,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:45'),(826,788,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:45'),(827,788,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:45'),(828,788,'108','TA_DA',0.0000,'2026-06-28 09:23:45'),(829,788,'109','PF',1800.0000,'2026-06-28 09:23:45'),(830,788,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:45'),(831,789,'101','BASIC',27500.0000,'2026-06-28 09:23:45'),(832,789,'102','HRA',11000.0000,'2026-06-28 09:23:45'),(833,789,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:45'),(834,789,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:45'),(835,789,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:45'),(836,789,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:45'),(837,789,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:45'),(838,789,'108','TA_DA',0.0000,'2026-06-28 09:23:45'),(839,789,'109','PF',1800.0000,'2026-06-28 09:23:45'),(840,789,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:45'),(841,790,'101','BASIC',27500.0000,'2026-06-28 09:23:45'),(842,790,'102','HRA',11000.0000,'2026-06-28 09:23:45'),(843,790,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:45'),(844,790,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:45'),(845,790,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:45'),(846,790,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:45'),(847,790,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:45'),(848,790,'108','TA_DA',0.0000,'2026-06-28 09:23:45'),(849,790,'109','PF',1800.0000,'2026-06-28 09:23:45'),(850,790,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:45'),(851,791,'101','BASIC',27500.0000,'2026-06-28 09:23:45'),(852,791,'102','HRA',11000.0000,'2026-06-28 09:23:45'),(853,791,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:45'),(854,791,'104','STAT_BONUS',2290.0000,'2026-06-28 09:23:45'),(855,791,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:45'),(856,791,'106','OTHER_ALLOW',10960.0000,'2026-06-28 09:23:45'),(857,791,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:45'),(858,791,'108','TA_DA',0.0000,'2026-06-28 09:23:45'),(859,791,'109','PF',1800.0000,'2026-06-28 09:23:45'),(860,791,'GROSS','GROSS',55000.0000,'2026-06-28 09:23:45'),(861,792,'101','BASIC',25000.0000,'2026-06-28 09:23:45'),(862,792,'102','HRA',10000.0000,'2026-06-28 09:23:45'),(863,792,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:45'),(864,792,'104','STAT_BONUS',2082.0000,'2026-06-28 09:23:45'),(865,792,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:45'),(866,792,'106','OTHER_ALLOW',9668.0000,'2026-06-28 09:23:45'),(867,792,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:45'),(868,792,'108','TA_DA',0.0000,'2026-06-28 09:23:45'),(869,792,'109','PF',1800.0000,'2026-06-28 09:23:45'),(870,792,'GROSS','GROSS',50000.0000,'2026-06-28 09:23:45'),(871,793,'101','BASIC',25000.0000,'2026-06-28 09:23:45'),(872,793,'102','HRA',10000.0000,'2026-06-28 09:23:45'),(873,793,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:45'),(874,793,'104','STAT_BONUS',2082.0000,'2026-06-28 09:23:45'),(875,793,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:45'),(876,793,'106','OTHER_ALLOW',9668.0000,'2026-06-28 09:23:45'),(877,793,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:45'),(878,793,'108','TA_DA',0.0000,'2026-06-28 09:23:45'),(879,793,'109','PF',1800.0000,'2026-06-28 09:23:45'),(880,793,'GROSS','GROSS',50000.0000,'2026-06-28 09:23:45'),(881,794,'101','BASIC',25000.0000,'2026-06-28 09:23:45'),(882,794,'102','HRA',10000.0000,'2026-06-28 09:23:45'),(883,794,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:45'),(884,794,'104','STAT_BONUS',2082.0000,'2026-06-28 09:23:45'),(885,794,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:45'),(886,794,'106','OTHER_ALLOW',9668.0000,'2026-06-28 09:23:45'),(887,794,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:45'),(888,794,'108','TA_DA',0.0000,'2026-06-28 09:23:45'),(889,794,'109','PF',1800.0000,'2026-06-28 09:23:45'),(890,794,'GROSS','GROSS',50000.0000,'2026-06-28 09:23:45'),(891,795,'101','BASIC',25000.0000,'2026-06-28 09:23:45'),(892,795,'102','HRA',10000.0000,'2026-06-28 09:23:45'),(893,795,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:45'),(894,795,'104','STAT_BONUS',2082.0000,'2026-06-28 09:23:45'),(895,795,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:45'),(896,795,'106','OTHER_ALLOW',9668.0000,'2026-06-28 09:23:45'),(897,795,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:46'),(898,795,'108','TA_DA',0.0000,'2026-06-28 09:23:46'),(899,795,'109','PF',1800.0000,'2026-06-28 09:23:46'),(900,795,'GROSS','GROSS',50000.0000,'2026-06-28 09:23:46'),(901,796,'101','BASIC',25000.0000,'2026-06-28 09:23:46'),(902,796,'102','HRA',10000.0000,'2026-06-28 09:23:46'),(903,796,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:46'),(904,796,'104','STAT_BONUS',2082.0000,'2026-06-28 09:23:46'),(905,796,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:46'),(906,796,'106','OTHER_ALLOW',9668.0000,'2026-06-28 09:23:46'),(907,796,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:46'),(908,796,'108','TA_DA',0.0000,'2026-06-28 09:23:46'),(909,796,'109','PF',1800.0000,'2026-06-28 09:23:46'),(910,796,'GROSS','GROSS',50000.0000,'2026-06-28 09:23:46'),(911,797,'101','BASIC',55000.0000,'2026-06-28 09:23:46'),(912,797,'102','HRA',22000.0000,'2026-06-28 09:23:46'),(913,797,'103','TEL_ALLOW',4000.0000,'2026-06-28 09:23:46'),(914,797,'104','STAT_BONUS',4581.5000,'2026-06-28 09:23:46'),(915,797,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:46'),(916,797,'106','OTHER_ALLOW',23168.0000,'2026-06-28 09:23:46'),(917,797,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:46'),(918,797,'108','TA_DA',0.0000,'2026-06-28 09:23:46'),(919,797,'109','PF',1800.0000,'2026-06-28 09:23:46'),(920,797,'GROSS','GROSS',110000.0000,'2026-06-28 09:23:46'),(921,798,'101','BASIC',19250.0000,'2026-06-28 09:23:46'),(922,798,'102','HRA',7700.0000,'2026-06-28 09:23:46'),(923,798,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:46'),(924,798,'104','STAT_BONUS',1603.0000,'2026-06-28 09:23:46'),(925,798,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:46'),(926,798,'106','OTHER_ALLOW',6697.0000,'2026-06-28 09:23:46'),(927,798,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:46'),(928,798,'108','TA_DA',0.0000,'2026-06-28 09:23:46'),(929,798,'109','PF',1800.0000,'2026-06-28 09:23:46'),(930,798,'GROSS','GROSS',38500.0000,'2026-06-28 09:23:46'),(931,799,'101','BASIC',19250.0000,'2026-06-28 09:23:46'),(932,799,'102','HRA',7700.0000,'2026-06-28 09:23:46'),(933,799,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:46'),(934,799,'104','STAT_BONUS',1603.0000,'2026-06-28 09:23:46'),(935,799,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:46'),(936,799,'106','OTHER_ALLOW',6697.0000,'2026-06-28 09:23:46'),(937,799,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:46'),(938,799,'108','TA_DA',0.0000,'2026-06-28 09:23:46'),(939,799,'109','PF',1800.0000,'2026-06-28 09:23:46'),(940,799,'GROSS','GROSS',38500.0000,'2026-06-28 09:23:46'),(941,800,'101','BASIC',19250.0000,'2026-06-28 09:23:46'),(942,800,'102','HRA',7700.0000,'2026-06-28 09:23:46'),(943,800,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:46'),(944,800,'104','STAT_BONUS',1603.0000,'2026-06-28 09:23:46'),(945,800,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:46'),(946,800,'106','OTHER_ALLOW',6697.0000,'2026-06-28 09:23:46'),(947,800,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:46'),(948,800,'108','TA_DA',0.0000,'2026-06-28 09:23:46'),(949,800,'109','PF',1800.0000,'2026-06-28 09:23:46'),(950,800,'GROSS','GROSS',38500.0000,'2026-06-28 09:23:46'),(951,801,'101','BASIC',17500.0000,'2026-06-28 09:23:46'),(952,801,'102','HRA',7000.0000,'2026-06-28 09:23:46'),(953,801,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:46'),(954,801,'104','STAT_BONUS',1457.0000,'2026-06-28 09:23:46'),(955,801,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:46'),(956,801,'106','OTHER_ALLOW',5793.0000,'2026-06-28 09:23:46'),(957,801,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:46'),(958,801,'108','TA_DA',0.0000,'2026-06-28 09:23:46'),(959,801,'109','PF',1800.0000,'2026-06-28 09:23:46'),(960,801,'GROSS','GROSS',35000.0000,'2026-06-28 09:23:46'),(961,802,'101','BASIC',13750.0000,'2026-06-28 09:23:46'),(962,802,'102','HRA',5500.0000,'2026-06-28 09:23:46'),(963,802,'103','TEL_ALLOW',2000.0000,'2026-06-28 09:23:46'),(964,802,'104','STAT_BONUS',1145.0000,'2026-06-28 09:23:46'),(965,802,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:46'),(966,802,'106','OTHER_ALLOW',3855.0000,'2026-06-28 09:23:46'),(967,802,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:46'),(968,802,'108','TA_DA',0.0000,'2026-06-28 09:23:46'),(969,802,'109','PF',1650.0000,'2026-06-28 09:23:46'),(970,802,'GROSS','GROSS',27500.0000,'2026-06-28 09:23:46'),(971,803,'101','BASIC',24000.0000,'2026-06-28 09:23:46'),(972,803,'102','HRA',9600.0000,'2026-06-28 09:23:46'),(973,803,'103','TEL_ALLOW',1000.0000,'2026-06-28 09:23:46'),(974,803,'104','STAT_BONUS',1999.0000,'2026-06-28 09:23:46'),(975,803,'105','MED_ALLOW',1250.0000,'2026-06-28 09:23:46'),(976,803,'106','OTHER_ALLOW',10151.0000,'2026-06-28 09:23:46'),(977,803,'107','LAPTOP_ALLOW',0.0000,'2026-06-28 09:23:46'),(978,803,'108','TA_DA',0.0000,'2026-06-28 09:23:46'),(979,803,'109','PF',1800.0000,'2026-06-28 09:23:46'),(980,803,'GROSS','GROSS',48000.0000,'2026-06-28 09:23:46');
/*!40000 ALTER TABLE `employeesalarycomponents` ENABLE KEYS */;

--
-- Table structure for table `essleaverequests`
--

DROP TABLE IF EXISTS `essleaverequests`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `essleaverequests` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `EmployeeId` int NOT NULL,
  `ClientId` int NOT NULL,
  `LeaveTypeId` int NOT NULL,
  `FromDate` date NOT NULL,
  `ToDate` date NOT NULL,
  `Days` decimal(8,2) NOT NULL,
  `Reason` varchar(1000) DEFAULT NULL,
  `Status` varchar(40) NOT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `essleaverequests`
--

/*!40000 ALTER TABLE `essleaverequests` DISABLE KEYS */;
/*!40000 ALTER TABLE `essleaverequests` ENABLE KEYS */;

--
-- Table structure for table `holiday_locations`
--

DROP TABLE IF EXISTS `holiday_locations`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `holiday_locations` (
  `id` int NOT NULL AUTO_INCREMENT,
  `holiday_id` int NOT NULL,
  `work_location_id` int NOT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_holiday_locations_holiday_location` (`holiday_id`,`work_location_id`),
  KEY `IX_holiday_locations_location` (`work_location_id`),
  CONSTRAINT `FK_holiday_locations_holiday` FOREIGN KEY (`holiday_id`) REFERENCES `holidays` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `holiday_locations`
--

/*!40000 ALTER TABLE `holiday_locations` DISABLE KEYS */;
/*!40000 ALTER TABLE `holiday_locations` ENABLE KEYS */;

--
-- Table structure for table `holidays`
--

DROP TABLE IF EXISTS `holidays`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `holidays` (
  `id` int NOT NULL AUTO_INCREMENT,
  `name` varchar(180) NOT NULL,
  `start_date` date NOT NULL,
  `end_date` date NOT NULL,
  `description` varchar(800) DEFAULT NULL,
  `all_locations` tinyint(1) NOT NULL DEFAULT '1',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `IX_holidays_dates` (`start_date`,`end_date`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `holidays`
--

/*!40000 ALTER TABLE `holidays` DISABLE KEYS */;
/*!40000 ALTER TABLE `holidays` ENABLE KEYS */;

--
-- Table structure for table `leave_attendance_preferences`
--

DROP TABLE IF EXISTS `leave_attendance_preferences`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `leave_attendance_preferences` (
  `id` int NOT NULL AUTO_INCREMENT,
  `attendance_cycle_start_day` int NOT NULL DEFAULT '1',
  `attendance_cycle_end_day` int NOT NULL DEFAULT '25',
  `payroll_report_generation_day` int NOT NULL DEFAULT '28',
  `include_leave_encashment_in_pay_run` tinyint(1) NOT NULL DEFAULT '0',
  `leave_encashment_salary_component_id` int DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_preferences_client` (`client_id`)
) ENGINE=InnoDB AUTO_INCREMENT=4 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `leave_attendance_preferences`
--

/*!40000 ALTER TABLE `leave_attendance_preferences` DISABLE KEYS */;
INSERT INTO `leave_attendance_preferences` VALUES (3,1,25,28,0,NULL,'2026-06-25 22:25:15','2026-06-25 22:25:15',7);
/*!40000 ALTER TABLE `leave_attendance_preferences` ENABLE KEYS */;

--
-- Table structure for table `leave_balance_import_errors`
--

DROP TABLE IF EXISTS `leave_balance_import_errors`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `leave_balance_import_errors` (
  `id` int NOT NULL AUTO_INCREMENT,
  `import_log_id` int NOT NULL,
  `row_no` int NOT NULL,
  `employee_number` varchar(80) DEFAULT NULL,
  `leave_type` varchar(180) DEFAULT NULL,
  `date_text` varchar(80) DEFAULT NULL,
  `count_text` varchar(80) DEFAULT NULL,
  `error_message` varchar(1000) NOT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  KEY `IX_leave_balance_import_errors_log` (`import_log_id`),
  CONSTRAINT `FK_leave_balance_import_errors_log` FOREIGN KEY (`import_log_id`) REFERENCES `leave_balance_import_logs` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `leave_balance_import_errors`
--

/*!40000 ALTER TABLE `leave_balance_import_errors` DISABLE KEYS */;
/*!40000 ALTER TABLE `leave_balance_import_errors` ENABLE KEYS */;

--
-- Table structure for table `leave_balance_import_logs`
--

DROP TABLE IF EXISTS `leave_balance_import_logs`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `leave_balance_import_logs` (
  `id` int NOT NULL AUTO_INCREMENT,
  `file_name` varchar(260) NOT NULL,
  `encoding` varchar(80) NOT NULL,
  `total_records` int NOT NULL DEFAULT '0',
  `imported_records` int NOT NULL DEFAULT '0',
  `skipped_records` int NOT NULL DEFAULT '0',
  `mapping_json` json DEFAULT NULL,
  `created_by` varchar(180) DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `leave_balance_import_logs`
--

/*!40000 ALTER TABLE `leave_balance_import_logs` DISABLE KEYS */;
/*!40000 ALTER TABLE `leave_balance_import_logs` ENABLE KEYS */;

--
-- Table structure for table `leave_type_applicability`
--

DROP TABLE IF EXISTS `leave_type_applicability`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `leave_type_applicability` (
  `id` int NOT NULL AUTO_INCREMENT,
  `leave_type_id` int NOT NULL,
  `applicability_mode` varchar(40) NOT NULL DEFAULT 'All employees',
  `work_location` varchar(150) DEFAULT NULL,
  `department` varchar(150) DEFAULT NULL,
  `designation` varchar(150) DEFAULT NULL,
  `gender` varchar(40) DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_leave_type_applicability_leave_type` (`leave_type_id`),
  CONSTRAINT `FK_leave_type_applicability_type` FOREIGN KEY (`leave_type_id`) REFERENCES `leave_types` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=14 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `leave_type_applicability`
--

/*!40000 ALTER TABLE `leave_type_applicability` DISABLE KEYS */;
INSERT INTO `leave_type_applicability` VALUES (9,11,'All employees','','','','','2026-06-25 22:51:49','2026-06-25 22:51:49'),(10,12,'All employees','','','','','2026-06-25 22:56:07','2026-06-25 22:56:07'),(11,13,'All employees','','','','','2026-06-25 22:58:04','2026-06-25 22:58:04'),(12,14,'All employees','','','','','2026-06-25 22:58:56','2026-06-25 22:58:56'),(13,15,'All employees','','','','','2026-06-26 17:27:01','2026-06-26 17:27:01');
/*!40000 ALTER TABLE `leave_type_applicability` ENABLE KEYS */;

--
-- Table structure for table `leave_type_policies`
--

DROP TABLE IF EXISTS `leave_type_policies`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `leave_type_policies` (
  `id` int NOT NULL AUTO_INCREMENT,
  `leave_type_id` int NOT NULL,
  `entitlement` decimal(10,2) NOT NULL DEFAULT '0.00',
  `entitlement_period` varchar(20) NOT NULL DEFAULT 'Yearly',
  `pro_rate_for_new_joinees` tinyint(1) NOT NULL DEFAULT '0',
  `reset_enabled` tinyint(1) NOT NULL DEFAULT '0',
  `reset_frequency` varchar(20) NOT NULL DEFAULT 'Yearly',
  `carry_forward_unused_leaves` tinyint(1) NOT NULL DEFAULT '0',
  `max_carry_forward_limit` decimal(10,2) DEFAULT NULL,
  `encash_unused_leaves` tinyint(1) NOT NULL DEFAULT '0',
  `max_encashment_limit` decimal(10,2) DEFAULT NULL,
  `allow_negative_leave_balance` tinyint(1) NOT NULL DEFAULT '0',
  `negative_balance_handling` varchar(50) NOT NULL DEFAULT 'Mark as LOP',
  `allow_past_dates` tinyint(1) NOT NULL DEFAULT '0',
  `past_date_limit_type` varchar(30) NOT NULL DEFAULT 'No limit',
  `past_date_limit_days` int DEFAULT NULL,
  `allow_future_dates` tinyint(1) NOT NULL DEFAULT '0',
  `future_date_limit_type` varchar(30) NOT NULL DEFAULT 'No limit',
  `future_date_limit_days` int DEFAULT NULL,
  `effective_from` date NOT NULL,
  `expires_on` date DEFAULT NULL,
  `postpone_credits_for_new_employees` tinyint(1) NOT NULL DEFAULT '0',
  `postpone_credit_value` int DEFAULT NULL,
  `postpone_credit_unit` varchar(20) NOT NULL DEFAULT 'Days',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_leave_type_policies_leave_type` (`leave_type_id`),
  CONSTRAINT `FK_leave_type_policies_type` FOREIGN KEY (`leave_type_id`) REFERENCES `leave_types` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=14 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `leave_type_policies`
--

/*!40000 ALTER TABLE `leave_type_policies` DISABLE KEYS */;
INSERT INTO `leave_type_policies` VALUES (9,11,12.00,'Yearly',0,1,'Yearly',0,NULL,0,NULL,0,'Mark as LOP',1,'No limit',NULL,1,'No limit',NULL,'2026-06-25','2026-06-26',0,NULL,'Days','2026-06-25 22:51:49','2026-06-25 22:51:49'),(10,12,12.00,'Yearly',0,0,'Yearly',0,NULL,0,NULL,1,'Mark as LOP',1,'No limit',NULL,1,'No limit',NULL,'2026-06-25','2026-06-27',0,NULL,'Days','2026-06-25 22:56:07','2026-06-25 22:56:07'),(11,13,18.00,'Yearly',1,1,'Yearly',1,45.00,1,10.00,0,'Mark as LOP',0,'No limit',NULL,1,'No limit',NULL,'2026-06-25','2026-06-30',0,NULL,'Days','2026-06-25 22:58:04','2026-06-25 22:58:04'),(12,14,0.00,'Yearly',0,1,'Yearly',0,NULL,0,NULL,0,'Mark as LOP',1,'No limit',NULL,0,'No limit',NULL,'2026-06-25','2026-06-30',0,NULL,'Days','2026-06-25 22:58:56','2026-06-25 22:58:56'),(13,15,90.00,'Yearly',1,0,'Yearly',0,NULL,0,NULL,0,'Mark as LOP',0,'No limit',NULL,0,'No limit',NULL,'2026-06-26','2026-06-30',0,NULL,'Days','2026-06-26 17:27:01','2026-06-26 17:27:01');
/*!40000 ALTER TABLE `leave_type_policies` ENABLE KEYS */;

--
-- Table structure for table `leave_types`
--

DROP TABLE IF EXISTS `leave_types`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `leave_types` (
  `id` int NOT NULL AUTO_INCREMENT,
  `name` varchar(180) NOT NULL,
  `code` varchar(40) NOT NULL,
  `type` varchar(20) NOT NULL DEFAULT 'Paid',
  `description` varchar(800) DEFAULT NULL,
  `is_active` tinyint(1) NOT NULL DEFAULT '1',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_leave_types_client_code` (`client_id`,`code`)
) ENGINE=InnoDB AUTO_INCREMENT=16 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `leave_types`
--

/*!40000 ALTER TABLE `leave_types` DISABLE KEYS */;
INSERT INTO `leave_types` VALUES (11,'Casual Leave','CL','Paid','For short-term personal tasks or unplanned absences.',1,'2026-06-25 22:51:49','2026-06-25 22:51:49',7),(12,'Sick Leave','SL','Paid','For medical recovery, illness, or doctor appointments.',1,'2026-06-25 22:56:07','2026-06-25 22:56:07',7),(13,'Earned Leave','EL','Paid','Monthly accrued leaves that can be carried forward or encashed.',1,'2026-06-25 22:58:04','2026-06-25 22:58:04',7),(14,'Compensatory Off','CO','Paid','Earned by employees for working on weekends or public holidays.',1,'2026-06-25 22:58:56','2026-06-25 22:58:56',7),(15,'leave without pay','LOP','Unpaid','',1,'2026-06-26 17:27:01','2026-06-26 17:27:01',7);
/*!40000 ALTER TABLE `leave_types` ENABLE KEYS */;

--
-- Table structure for table `modulesettings`
--

DROP TABLE IF EXISTS `modulesettings`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `modulesettings` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ModuleCode` varchar(80) NOT NULL,
  `IsEnabled` tinyint(1) NOT NULL DEFAULT '0',
  `SettingsJson` json DEFAULT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_ModuleSettings_ModuleCode` (`ModuleCode`),
  UNIQUE KEY `UX_ModuleSettings_Client_Module` (`client_id`,`ModuleCode`)
) ENGINE=InnoDB AUTO_INCREMENT=141 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `modulesettings`
--

/*!40000 ALTER TABLE `modulesettings` DISABLE KEYS */;
/*!40000 ALTER TABLE `modulesettings` ENABLE KEYS */;

--
-- Table structure for table `modulesetupprogress`
--

DROP TABLE IF EXISTS `modulesetupprogress`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `modulesetupprogress` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ModuleCode` varchar(80) NOT NULL,
  `StepCode` varchar(80) NOT NULL,
  `Title` varchar(180) NOT NULL,
  `Description` varchar(600) DEFAULT NULL,
  `Status` varchar(40) NOT NULL DEFAULT 'Not Started',
  `IsMandatory` tinyint(1) NOT NULL DEFAULT '0',
  `CanDisable` tinyint(1) NOT NULL DEFAULT '1',
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_ModuleSetupProgress_Module_Step` (`ModuleCode`,`StepCode`),
  UNIQUE KEY `UX_ModuleSetupProgress_Client_Module_Step` (`client_id`,`ModuleCode`,`StepCode`),
  KEY `IX_ModuleSetupProgress_ModuleCode` (`ModuleCode`)
) ENGINE=InnoDB AUTO_INCREMENT=701 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `modulesetupprogress`
--

/*!40000 ALTER TABLE `modulesetupprogress` DISABLE KEYS */;
/*!40000 ALTER TABLE `modulesetupprogress` ENABLE KEYS */;

--
-- Table structure for table `organizations`
--

DROP TABLE IF EXISTS `organizations`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `organizations` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Name` varchar(250) NOT NULL,
  `LegalName` varchar(250) DEFAULT NULL,
  `BusinessType` varchar(150) DEFAULT NULL,
  `BusinessLocation` varchar(100) NOT NULL DEFAULT 'India',
  `Industry` varchar(150) DEFAULT NULL,
  `HasRunPayrollThisYear` tinyint(1) NOT NULL DEFAULT '0',
  `SetupCompleted` tinyint(1) NOT NULL DEFAULT '0',
  `PAN` varchar(50) DEFAULT NULL,
  `GSTIN` varchar(50) DEFAULT NULL,
  `FiscalYearStart` varchar(50) DEFAULT NULL,
  `AddressLine1` varchar(255) DEFAULT NULL,
  `AddressLine2` varchar(255) DEFAULT NULL,
  `City` varchar(100) DEFAULT NULL,
  `State` varchar(100) DEFAULT NULL,
  `PostalCode` varchar(30) DEFAULT NULL,
  `Country` varchar(100) DEFAULT NULL,
  `BankName` varchar(200) DEFAULT NULL,
  `AccountNumber` varchar(100) DEFAULT NULL,
  `IFSCCode` varchar(50) DEFAULT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `LogoDataUrl` longtext,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `organizations`
--

/*!40000 ALTER TABLE `organizations` DISABLE KEYS */;
INSERT INTO `organizations` VALUES (2,'Demo Payroll Pvt Ltd','Demo Payroll Private Limited','Private Limited Company','India','Information Technology',0,1,'','','','221B Business Park','','Bengaluru','Karnataka','560001','India','','','','2026-06-25 22:18:55','2026-06-28 01:27:38','');
/*!40000 ALTER TABLE `organizations` ENABLE KEYS */;

--
-- Table structure for table `payrolladjustments`
--

DROP TABLE IF EXISTS `payrolladjustments`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `payrolladjustments` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ClientId` int NOT NULL,
  `EmployeeId` int NOT NULL,
  `EmployeeName` varchar(250) NOT NULL DEFAULT '',
  `EmployeeCode` varchar(50) NOT NULL DEFAULT '',
  `ComponentId` int NOT NULL DEFAULT '0',
  `ComponentCode` varchar(50) NOT NULL DEFAULT '',
  `ComponentName` varchar(150) NOT NULL,
  `AdjustmentType` varchar(30) NOT NULL,
  `Amount` decimal(18,2) NOT NULL,
  `PayPeriod` varchar(7) NOT NULL,
  `PayRunType` varchar(30) NOT NULL DEFAULT 'Regular',
  `ReasonCode` varchar(80) NOT NULL DEFAULT '',
  `Notes` varchar(500) NOT NULL DEFAULT '',
  `Taxable` tinyint(1) NOT NULL DEFAULT '1',
  `Status` varchar(30) NOT NULL DEFAULT 'Approved',
  `PayRunId` int DEFAULT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  KEY `IX_PayrollAdjustments_Client_Period_Status` (`ClientId`,`PayPeriod`,`Status`),
  KEY `FK_PayrollAdjustments_Employees` (`EmployeeId`),
  KEY `FK_PayrollAdjustments_PayRuns` (`PayRunId`),
  CONSTRAINT `FK_PayrollAdjustments_Employees` FOREIGN KEY (`EmployeeId`) REFERENCES `employees` (`Id`),
  CONSTRAINT `FK_PayrollAdjustments_PayRuns` FOREIGN KEY (`PayRunId`) REFERENCES `payruns` (`Id`) ON DELETE SET NULL
) ENGINE=InnoDB AUTO_INCREMENT=4 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `payrolladjustments`
--

/*!40000 ALTER TABLE `payrolladjustments` DISABLE KEYS */;
INSERT INTO `payrolladjustments` VALUES (3,10,779,'Neeraj Kumar','RECL 310',0,'TDS','TDS','Deduction',5200.00,'2026-05','Regular','RECL_EXCEL_TDS_SEED','Seeded from RECL DATA.xlsx TDS column',0,'Approved',NULL,'2026-06-28 07:45:27','2026-06-28 07:45:27');
/*!40000 ALTER TABLE `payrolladjustments` ENABLE KEYS */;

--
-- Table structure for table `payrollschedules`
--

DROP TABLE IF EXISTS `payrollschedules`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `payrollschedules` (
  `Id` int NOT NULL DEFAULT '1',
  `WorkWeek` varchar(80) NOT NULL DEFAULT 'Monday - Friday',
  `SalaryDays` varchar(80) NOT NULL DEFAULT 'Actual days',
  `FixedDays` varchar(10) NOT NULL DEFAULT '30',
  `PayDay` varchar(80) NOT NULL DEFAULT 'Last working day',
  `FirstPayPeriod` varchar(7) NOT NULL DEFAULT '',
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `payrollschedules`
--

/*!40000 ALTER TABLE `payrollschedules` DISABLE KEYS */;
INSERT INTO `payrollschedules` VALUES (1,'Monday - Friday','Actual days','30','Last working day','2026-06','2026-06-28 09:23:38');
/*!40000 ALTER TABLE `payrollschedules` ENABLE KEYS */;

--
-- Table structure for table `payrollsetups`
--

DROP TABLE IF EXISTS `payrollsetups`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `payrollsetups` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `SetupJson` json NOT NULL,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `payrollsetups`
--

/*!40000 ALTER TABLE `payrollsetups` DISABLE KEYS */;
INSERT INTO `payrollsetups` VALUES (2,'{\"tax\": {\"pan\": \"ABCDE1234F\", \"tan\": \"ABCD12345E\", \"slabs\": [{\"id\": 1, \"active\": true, \"regime\": \"New\", \"incomeTo\": \"\", \"incomeFrom\": \"0\", \"ratePercent\": \"0\", \"effectiveFrom\": \"2026-06-27\", \"financialYear\": \"2026-27\"}], \"aoCode\": \"BLR/W/123/1\", \"frequency\": \"Monthly\", \"surcharges\": [{\"id\": 1, \"active\": true, \"incomeTo\": \"5000000\", \"incomeFrom\": \"0\", \"financialYear\": \"2026-27\", \"surchargePercent\": \"0\"}, {\"id\": 2, \"active\": true, \"incomeTo\": \"10000000\", \"incomeFrom\": \"5000000\", \"financialYear\": \"2026-27\", \"surchargePercent\": \"10\"}, {\"id\": 3, \"active\": true, \"incomeTo\": \"20000000\", \"incomeFrom\": \"10000000\", \"financialYear\": \"2026-27\", \"surchargePercent\": \"15\"}, {\"id\": 4, \"active\": true, \"incomeTo\": \"50000000\", \"incomeFrom\": \"20000000\", \"financialYear\": \"2026-27\", \"surchargePercent\": \"25\"}, {\"id\": 5, \"active\": true, \"incomeTo\": \"\", \"incomeFrom\": \"50000000\", \"financialYear\": \"2026-27\", \"surchargePercent\": \"37\"}], \"clientSettings\": [{\"id\": 9301, \"active\": true, \"enabled\": true, \"clientId\": \"10:RECL\", \"defaultRegime\": \"New\", \"financialYear\": \"2026-27\", \"requireApproval\": true, \"allowDeclarations\": true, \"lockAfterApproval\": true, \"projectMonthlyTds\": true, \"reminderFrequency\": \"Weekly\", \"poiProcessingMonth\": \"2026-05\", \"requireProofUpload\": true, \"actualDeclarationEnd\": \"\", \"declarationWindowEnd\": \"\", \"plannedDeclarationEnd\": \"\", \"regimeSelectionCutoff\": \"\", \"reminderEmailsEnabled\": true, \"actualDeclarationStart\": \"\", \"declarationWindowStart\": \"\", \"reminderBeforeLockDays\": \"7\", \"plannedDeclarationStart\": \"\", \"regimeSelectionWindowOpen\": false, \"taxDeductionComponentCode\": \"TDS\", \"actualDeclarationWindowOpen\": false, \"allowEmployeeRegimeSelection\": true, \"plannedDeclarationWindowOpen\": false}], \"finalAdjustments\": [{\"id\": 1, \"label\": \"Health & Education Cess\", \"value\": \"4\", \"active\": true, \"valueType\": \"Percent\", \"applyOrder\": \"100\", \"financialYear\": \"2026-27\"}], \"declarationSections\": [{\"id\": 1, \"code\": \"80C\", \"name\": \"Section 80C investments\", \"active\": true, \"regime\": \"Old\", \"limitAmount\": \"150000\", \"financialYear\": \"2026-27\", \"proofRequired\": true, \"requiresApproval\": true}, {\"id\": 2, \"code\": \"80CCD1B\", \"name\": \"NPS employee contribution\", \"active\": true, \"regime\": \"Old\", \"limitAmount\": \"50000\", \"financialYear\": \"2026-27\", \"proofRequired\": true, \"requiresApproval\": true}, {\"id\": 3, \"code\": \"80D\", \"name\": \"Medical insurance premium\", \"active\": true, \"regime\": \"Old\", \"limitAmount\": \"25000\", \"financialYear\": \"2026-27\", \"proofRequired\": true, \"requiresApproval\": true}, {\"id\": 4, \"code\": \"HRA\", \"name\": \"House rent allowance\", \"active\": true, \"regime\": \"Old\", \"limitAmount\": \"\", \"financialYear\": \"2026-27\", \"proofRequired\": true, \"requiresApproval\": true}, {\"id\": 5, \"code\": \"24B\", \"name\": \"Home loan interest\", \"active\": true, \"regime\": \"Old\", \"limitAmount\": \"200000\", \"financialYear\": \"2026-27\", \"proofRequired\": true, \"requiresApproval\": true}, {\"id\": 6, \"code\": \"80E\", \"name\": \"Education loan interest\", \"active\": true, \"regime\": \"Old\", \"limitAmount\": \"\", \"financialYear\": \"2026-27\", \"proofRequired\": true, \"requiresApproval\": true}, {\"id\": 7, \"code\": \"80G\", \"name\": \"Donations\", \"active\": true, \"regime\": \"Old\", \"limitAmount\": \"\", \"financialYear\": \"2026-27\", \"proofRequired\": true, \"requiresApproval\": true}, {\"id\": 8, \"code\": \"OTHER\", \"name\": \"Other exemptions and deductions\", \"active\": true, \"regime\": \"Old\", \"limitAmount\": \"\", \"financialYear\": \"2026-27\", \"proofRequired\": true, \"requiresApproval\": true}]}, \"schedule\": {\"payDay\": \"Last working day\", \"workWeek\": \"Monday - Friday\", \"fixedDays\": \"30\", \"salaryDays\": \"Actual days\", \"firstPayPeriod\": \"2026-06\"}, \"statutory\": {\"pt\": true, \"epf\": true, \"esi\": false, \"lwf\": true, \"abry\": false, \"epfCtc\": true, \"ptCycle\": \"Monthly\", \"ptSlabs\": \"Up to 15000: 0\\n15001 and above: 200\", \"ptState\": \"Karnataka\", \"lwfCycle\": \"Half-yearly\", \"lwfState\": \"Karnataka\", \"ptNumber\": \"PT-KA-12345\", \"epfNumber\": \"BG/BNG/1234567\", \"esiNumber\": \"\", \"restrictPf\": true, \"ptStateSlabs\": [{\"id\": 5001, \"notes\": \"RECL Excel AC slab\", \"state\": \"Karnataka\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"5999\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5002, \"notes\": \"RECL Excel AC slab\", \"state\": \"Karnataka\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"8999\", \"salaryFrom\": \"6000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"80\"}, {\"id\": 5003, \"notes\": \"RECL Excel AC slab\", \"state\": \"Karnataka\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"11999\", \"salaryFrom\": \"9000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"150\"}, {\"id\": 5004, \"notes\": \"RECL Excel AC slab\", \"state\": \"Karnataka\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"12000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"200\"}, {\"id\": 5005, \"notes\": \"RECL Excel AC slab\", \"state\": \"Gujarat\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"5999\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5006, \"notes\": \"RECL Excel AC slab\", \"state\": \"Gujarat\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"8999\", \"salaryFrom\": \"6000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"80\"}, {\"id\": 5007, \"notes\": \"RECL Excel AC slab\", \"state\": \"Gujarat\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"11999\", \"salaryFrom\": \"9000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"150\"}, {\"id\": 5008, \"notes\": \"RECL Excel AC slab\", \"state\": \"Gujarat\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"12000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"200\"}, {\"id\": 5009, \"notes\": \"RECL Excel AC slab\", \"state\": \"Andhra Pradesh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"5999\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5010, \"notes\": \"RECL Excel AC slab\", \"state\": \"Andhra Pradesh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"8999\", \"salaryFrom\": \"6000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"80\"}, {\"id\": 5011, \"notes\": \"RECL Excel AC slab\", \"state\": \"Andhra Pradesh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"11999\", \"salaryFrom\": \"9000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"150\"}, {\"id\": 5012, \"notes\": \"RECL Excel AC slab\", \"state\": \"Andhra Pradesh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"12000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"200\"}, {\"id\": 5013, \"notes\": \"RECL Excel AC slab\", \"state\": \"Madhya Pradesh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"18750\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5014, \"notes\": \"RECL Excel AC slab\", \"state\": \"Madhya Pradesh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"25000\", \"salaryFrom\": \"18751\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"125\"}, {\"id\": 5015, \"notes\": \"RECL Excel AC slab\", \"state\": \"Madhya Pradesh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"33333\", \"salaryFrom\": \"25001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"167\"}, {\"id\": 5016, \"notes\": \"RECL Excel AC slab\", \"state\": \"Madhya Pradesh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"33334\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"208\"}, {\"id\": 5017, \"notes\": \"RECL Excel AC slab\", \"state\": \"Assam\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"10000\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5018, \"notes\": \"RECL Excel AC slab\", \"state\": \"Assam\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"15000\", \"salaryFrom\": \"10001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"150\"}, {\"id\": 5019, \"notes\": \"RECL Excel AC slab\", \"state\": \"Assam\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"25000\", \"salaryFrom\": \"15001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"180\"}, {\"id\": 5020, \"notes\": \"RECL Excel AC slab\", \"state\": \"Assam\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"25001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"208\"}, {\"id\": 5021, \"notes\": \"RECL Excel AC slab\", \"state\": \"Rajasthan\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"10000\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5022, \"notes\": \"RECL Excel AC slab\", \"state\": \"Rajasthan\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"15000\", \"salaryFrom\": \"10001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"150\"}, {\"id\": 5023, \"notes\": \"RECL Excel AC slab\", \"state\": \"Rajasthan\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"25000\", \"salaryFrom\": \"15001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"180\"}, {\"id\": 5024, \"notes\": \"RECL Excel AC slab\", \"state\": \"Rajasthan\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"25001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"208\"}, {\"id\": 5025, \"notes\": \"RECL Excel AC slab\", \"state\": \"Jammu u0026 Kashmir\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"7500\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5026, \"notes\": \"RECL Excel AC slab\", \"state\": \"Jammu u0026 Kashmir\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"10000\", \"salaryFrom\": \"7501\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"175\"}, {\"id\": 5027, \"notes\": \"RECL Excel AC slab\", \"state\": \"Jammu u0026 Kashmir\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"10001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"200\"}, {\"id\": 5028, \"notes\": \"RECL Excel AC slab\", \"state\": \"Maharashtra\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"7500\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5029, \"notes\": \"RECL Excel AC slab\", \"state\": \"Maharashtra\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"10000\", \"salaryFrom\": \"7501\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"175\"}, {\"id\": 5030, \"notes\": \"RECL Excel AC slab\", \"state\": \"Maharashtra\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"10001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"200\"}, {\"id\": 5031, \"notes\": \"RECL Excel AC slab\", \"state\": \"West Bengal\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"10000\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5032, \"notes\": \"RECL Excel AC slab\", \"state\": \"West Bengal\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"15000\", \"salaryFrom\": \"10001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"110\"}, {\"id\": 5033, \"notes\": \"RECL Excel AC slab\", \"state\": \"West Bengal\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"25000\", \"salaryFrom\": \"15001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"130\"}, {\"id\": 5034, \"notes\": \"RECL Excel AC slab\", \"state\": \"West Bengal\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"40000\", \"salaryFrom\": \"25001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"150\"}, {\"id\": 5035, \"notes\": \"RECL Excel AC slab\", \"state\": \"West Bengal\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"40001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"200\"}, {\"id\": 5036, \"notes\": \"RECL Excel AC slab\", \"state\": \"Uttar Pradesh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"10000\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5037, \"notes\": \"RECL Excel AC slab\", \"state\": \"Uttar Pradesh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"15000\", \"salaryFrom\": \"10001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"110\"}, {\"id\": 5038, \"notes\": \"RECL Excel AC slab\", \"state\": \"Uttar Pradesh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"25000\", \"salaryFrom\": \"15001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"130\"}, {\"id\": 5039, \"notes\": \"RECL Excel AC slab\", \"state\": \"Uttar Pradesh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"40000\", \"salaryFrom\": \"25001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"150\"}, {\"id\": 5040, \"notes\": \"RECL Excel AC slab\", \"state\": \"Uttar Pradesh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"40001\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"200\"}, {\"id\": 5041, \"notes\": \"RECL Excel AC slab\", \"state\": \"Bihar\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"24999\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5042, \"notes\": \"RECL Excel AC slab\", \"state\": \"Bihar\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"41666\", \"salaryFrom\": \"25000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"83\"}, {\"id\": 5043, \"notes\": \"RECL Excel AC slab\", \"state\": \"Bihar\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"41667\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"167\"}, {\"id\": 5044, \"notes\": \"RECL Excel AC slab\", \"state\": \"Jharkhand\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"24999\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5045, \"notes\": \"RECL Excel AC slab\", \"state\": \"Jharkhand\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"41667\", \"salaryFrom\": \"25000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"100\"}, {\"id\": 5046, \"notes\": \"RECL Excel AC slab\", \"state\": \"Jharkhand\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"66667\", \"salaryFrom\": \"41668\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"150\"}, {\"id\": 5047, \"notes\": \"RECL Excel AC slab\", \"state\": \"Jharkhand\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"83333\", \"salaryFrom\": \"66668\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"175\"}, {\"id\": 5048, \"notes\": \"RECL Excel AC slab\", \"state\": \"Jharkhand\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"83334\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"208\"}, {\"id\": 5049, \"notes\": \"RECL Excel AC slab\", \"state\": \"Kerala\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"11999\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5050, \"notes\": \"RECL Excel AC slab\", \"state\": \"Kerala\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"17999\", \"salaryFrom\": \"12000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"120\"}, {\"id\": 5051, \"notes\": \"RECL Excel AC slab\", \"state\": \"Kerala\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"29999\", \"salaryFrom\": \"18000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"180\"}, {\"id\": 5052, \"notes\": \"RECL Excel AC slab\", \"state\": \"Kerala\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"44999\", \"salaryFrom\": \"30000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"300\"}, {\"id\": 5053, \"notes\": \"RECL Excel AC slab\", \"state\": \"Kerala\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"45000\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"450\"}, {\"id\": 5054, \"notes\": \"RECL Excel AC blank/zero; confirm statutory bucket\", \"state\": \"Haryana\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5055, \"notes\": \"RECL Excel AC blank/zero; confirm statutory bucket\", \"state\": \"Delhi\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5056, \"notes\": \"RECL Excel AC blank/zero; confirm statutory bucket\", \"state\": \"Chhattisgarh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5057, \"notes\": \"RECL Excel AC blank/zero; confirm statutory bucket\", \"state\": \"Himachal Pradesh\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}, {\"id\": 5058, \"notes\": \"RECL Excel AC blank/zero; confirm statutory bucket\", \"state\": \"Tamil Nadu\", \"active\": true, \"gender\": \"All\", \"salaryTo\": \"\", \"salaryFrom\": \"0\", \"effectiveTo\": \"\", \"effectiveFrom\": \"2026-04-01\", \"deductionAmount\": \"0\"}], \"epfContribution\": \"Both Employee and Employer\", \"lwfEligibilityLimit\": \"15000\", \"reclStatutoryMappings\": [{\"city\": \"Bengaluru\", \"state\": \"Karnataka\", \"bucket\": \"Professional Tax\", \"client\": \"RECL\", \"district\": \"Bengaluru Urban\", \"location\": \"Bangalore\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Baddi-Barotiwala-Nalagarh\", \"state\": \"Himachal Pradesh\", \"bucket\": \"No value in Excel AC\", \"client\": \"RECL\", \"district\": \"Solan\", \"location\": \"BBN\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Bhopal\", \"state\": \"Madhya Pradesh\", \"bucket\": \"Professional Tax\", \"client\": \"RECL\", \"district\": \"Bhopal\", \"location\": \"Bhopal\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Chennai\", \"state\": \"Tamil Nadu\", \"bucket\": \"No value in Excel AC\", \"client\": \"RECL\", \"district\": \"Chennai\", \"location\": \"Chennai\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Gurugram\", \"state\": \"Haryana\", \"bucket\": \"No value in Excel AC\", \"client\": \"RECL\", \"district\": \"Gurugram\", \"location\": \"CO, Gurugram\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Guwahati\", \"state\": \"Assam\", \"bucket\": \"Professional Tax\", \"client\": \"RECL\", \"district\": \"Kamrup Metropolitan\", \"location\": \"GUWAHATI\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Jaipur\", \"state\": \"Rajasthan\", \"bucket\": \"Professional Tax\", \"client\": \"RECL\", \"district\": \"Jaipur\", \"location\": \"JAIPUR\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Jammu\", \"state\": \"Jammu u0026 Kashmir\", \"bucket\": \"Professional Tax\", \"client\": \"RECL\", \"district\": \"Jammu\", \"location\": \"JAMMU\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Kolkata\", \"state\": \"West Bengal\", \"bucket\": \"Professional Tax\", \"client\": \"RECL\", \"district\": \"Kolkata\", \"location\": \"KOLKATA\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Lucknow\", \"state\": \"Uttar Pradesh\", \"bucket\": \"Professional Tax\", \"client\": \"RECL\", \"district\": \"Lucknow\", \"location\": \"Lucknow\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"New Delhi\", \"state\": \"Delhi\", \"bucket\": \"No value in Excel AC\", \"client\": \"RECL\", \"district\": \"New Delhi\", \"location\": \"MNRE\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Mumbai\", \"state\": \"Maharashtra\", \"bucket\": \"Professional Tax\", \"client\": \"RECL\", \"district\": \"Mumbai\", \"location\": \"Mumbai\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Panchkula\", \"state\": \"Haryana\", \"bucket\": \"No value in Excel AC\", \"client\": \"RECL\", \"district\": \"Panchkula\", \"location\": \"Panchkula\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Patna\", \"state\": \"Bihar\", \"bucket\": \"Professional Tax\", \"client\": \"RECL\", \"district\": \"Patna\", \"location\": \"Patna\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Raipur\", \"state\": \"Chhattisgarh\", \"bucket\": \"No value in Excel AC\", \"client\": \"RECL\", \"district\": \"Raipur\", \"location\": \"RAIPUR\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Ranchi\", \"state\": \"Jharkhand\", \"bucket\": \"Professional Tax\", \"client\": \"RECL\", \"district\": \"Ranchi\", \"location\": \"Ranchi\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"New Delhi\", \"state\": \"Delhi\", \"bucket\": \"No value in Excel AC\", \"client\": \"RECL\", \"district\": \"New Delhi\", \"location\": \"SCOPE Complex, New Delhi\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Shimla\", \"state\": \"Himachal Pradesh\", \"bucket\": \"No value in Excel AC\", \"client\": \"RECL\", \"district\": \"Shimla\", \"location\": \"Shimla\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Thiruvananthapuram\", \"state\": \"Kerala\", \"bucket\": \"Professional Tax\", \"client\": \"RECL\", \"district\": \"Thiruvananthapuram\", \"location\": \"Thiruvananthapuram\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Vadodara\", \"state\": \"Gujarat\", \"bucket\": \"Professional Tax\", \"client\": \"RECL\", \"district\": \"Vadodara\", \"location\": \"Vadodra\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}, {\"city\": \"Vijayawada\", \"state\": \"Andhra Pradesh\", \"bucket\": \"Professional Tax\", \"client\": \"RECL\", \"district\": \"NTR\", \"location\": \"Vijayawada\", \"sourceColumn\": \"Workmen Comp/LWF/PT\", \"calculationStatus\": \"Seeded mapping only; engine integration pending\"}], \"workmenCompConfigured\": false, \"lwfEmployeeContribution\": \"20\", \"lwfEmployerContribution\": \"40\"}, \"payslipTemplates\": [{\"id\": 301, \"name\": \"Acme Classic Payslip\", \"note\": \"This is a system generated payslip.\", \"theme\": \"Classic\", \"active\": true, \"showYtd\": true, \"clientId\": \"1:Acme Technologies\", \"showBank\": true, \"showLogo\": true, \"showClient\": true}], \"salaryComponents\": [{\"id\": 101, \"ctc\": true, \"epf\": \"Always\", \"esi\": true, \"fbp\": false, \"code\": \"BASIC\", \"name\": \"Basic\", \"value\": \"\", \"active\": true, \"formula\": \"GROSS * 50%\", \"payType\": \"Fixed Pay\", \"proRata\": true, \"taxable\": true, \"category\": \"Earning\", \"priority\": \"10\", \"recurring\": true, \"scheduled\": false, \"restrictFbp\": false, \"correctionOf\": \"\", \"baseComponent\": \"GROSS\", \"componentType\": \"Basic\", \"investmentType\": \"\", \"calculationType\": \"Formula\"}, {\"id\": 102, \"ctc\": true, \"epf\": \"Never\", \"esi\": true, \"fbp\": false, \"code\": \"HRA\", \"name\": \"HRA\", \"value\": \"\", \"active\": true, \"formula\": \"BASIC * 40%\", \"payType\": \"Fixed Pay\", \"proRata\": true, \"taxable\": true, \"category\": \"Earning\", \"priority\": \"20\", \"recurring\": true, \"scheduled\": false, \"restrictFbp\": false, \"correctionOf\": \"\", \"baseComponent\": \"BASIC\", \"componentType\": \"House Rent Allowance\", \"investmentType\": \"\", \"calculationType\": \"Formula\"}, {\"id\": 103, \"ctc\": true, \"epf\": \"Never\", \"esi\": true, \"fbp\": false, \"code\": \"TEL_ALLOW\", \"name\": \"Telephonic Allowance\", \"value\": \"2000\", \"active\": true, \"formula\": \"\", \"payType\": \"Fixed Pay\", \"proRata\": true, \"taxable\": true, \"category\": \"Earning\", \"priority\": \"30\", \"recurring\": true, \"scheduled\": false, \"restrictFbp\": false, \"correctionOf\": \"\", \"baseComponent\": \"\", \"componentType\": \"Telephone\", \"investmentType\": \"\", \"calculationType\": \"Fixed Amount\"}, {\"id\": 104, \"ctc\": true, \"epf\": \"Never\", \"esi\": true, \"fbp\": false, \"code\": \"STAT_BONUS\", \"name\": \"Statutory Bonus\", \"value\": \"\", \"active\": true, \"formula\": \"ROUNDDOWN(BASIC * 8.33%)\", \"payType\": \"Fixed Pay\", \"proRata\": true, \"taxable\": true, \"category\": \"Earning\", \"priority\": \"40\", \"recurring\": true, \"scheduled\": false, \"restrictFbp\": false, \"correctionOf\": \"\", \"baseComponent\": \"BASIC\", \"componentType\": \"Bonus\", \"investmentType\": \"\", \"calculationType\": \"Formula\"}, {\"id\": 105, \"ctc\": true, \"epf\": \"Never\", \"esi\": true, \"fbp\": false, \"code\": \"MED_ALLOW\", \"name\": \"Medical Allowance\", \"value\": \"1250\", \"active\": true, \"formula\": \"\", \"payType\": \"Fixed Pay\", \"proRata\": true, \"taxable\": true, \"category\": \"Earning\", \"priority\": \"50\", \"recurring\": true, \"scheduled\": false, \"restrictFbp\": false, \"correctionOf\": \"\", \"baseComponent\": \"\", \"componentType\": \"Medical Allowance\", \"investmentType\": \"\", \"calculationType\": \"Fixed Amount\"}, {\"id\": 106, \"ctc\": true, \"epf\": \"Never\", \"esi\": true, \"fbp\": false, \"code\": \"OTHER_ALLOW\", \"name\": \"Other Allowance\", \"value\": \"\", \"active\": true, \"formula\": \"GROSS - SUM(Fixed Earnings)\", \"payType\": \"Fixed Pay\", \"proRata\": true, \"taxable\": true, \"category\": \"Earning\", \"priority\": \"60\", \"recurring\": true, \"scheduled\": false, \"restrictFbp\": false, \"correctionOf\": \"\", \"baseComponent\": \"GROSS\", \"componentType\": \"Custom Allowance\", \"investmentType\": \"\", \"calculationType\": \"Residual / Balancing\"}, {\"id\": 107, \"ctc\": true, \"epf\": \"Never\", \"esi\": true, \"fbp\": false, \"code\": \"LAPTOP_ALLOW\", \"name\": \"Laptop Allowance\", \"value\": \"2000\", \"active\": true, \"formula\": \"\", \"payType\": \"Fixed Pay\", \"proRata\": true, \"taxable\": true, \"category\": \"Earning\", \"priority\": \"70\", \"recurring\": true, \"scheduled\": false, \"restrictFbp\": false, \"correctionOf\": \"\", \"baseComponent\": \"\", \"componentType\": \"Custom Allowance\", \"investmentType\": \"\", \"calculationType\": \"Fixed Amount\"}, {\"id\": 108, \"ctc\": false, \"epf\": \"Never\", \"esi\": true, \"fbp\": false, \"code\": \"TA_DA\", \"name\": \"TA/DA\", \"value\": \"\", \"active\": true, \"formula\": \"\", \"payType\": \"Variable Pay\", \"proRata\": true, \"taxable\": true, \"category\": \"Earning\", \"priority\": \"80\", \"recurring\": false, \"scheduled\": true, \"restrictFbp\": false, \"correctionOf\": \"\", \"baseComponent\": \"\", \"componentType\": \"Custom Allowance\", \"investmentType\": \"\", \"calculationType\": \"Manual / Variable\"}, {\"id\": 109, \"ctc\": true, \"epf\": \"Never\", \"esi\": false, \"fbp\": false, \"code\": \"PF\", \"name\": \"Provident Fund\", \"value\": \"\", \"active\": true, \"formula\": \"MIN(BASIC, 15000) * 12%\", \"payType\": \"Fixed Pay\", \"proRata\": true, \"taxable\": false, \"category\": \"Deduction\", \"priority\": \"110\", \"recurring\": true, \"scheduled\": false, \"restrictFbp\": false, \"correctionOf\": \"\", \"baseComponent\": \"BASIC\", \"componentType\": \"Provident Fund\", \"investmentType\": \"\", \"calculationType\": \"Formula\"}], \"salaryStructures\": [{\"id\": 201, \"name\": \"Acme Default CTC\", \"lines\": [{\"value\": \"GROSS * 50%\", \"componentId\": \"101\"}, {\"value\": \"BASIC * 40%\", \"componentId\": \"102\"}, {\"value\": \"2000\", \"componentId\": \"103\"}, {\"value\": \"ROUNDDOWN(BASIC * 8.33%)\", \"componentId\": \"104\"}, {\"value\": \"1250\", \"componentId\": \"105\"}, {\"value\": \"GROSS - SUM(Fixed Earnings)\", \"componentId\": \"106\"}, {\"value\": \"2000\", \"componentId\": \"107\"}, {\"value\": \"\", \"componentId\": \"108\"}, {\"value\": \"MIN(BASIC,15000)*12%\", \"componentId\": \"109\"}], \"active\": true, \"clientId\": \"1:Acme Technologies\", \"annualCtc\": \"900000\"}, {\"id\": 9201, \"name\": \"RECL Standard - Tel 2000\", \"lines\": [{\"value\": \"GROSS * 50%\", \"componentId\": \"101\"}, {\"value\": \"BASIC * 40%\", \"componentId\": \"102\"}, {\"value\": 2000, \"componentId\": \"103\"}, {\"value\": \"ROUNDDOWN(BASIC * 8.33%)\", \"componentId\": \"104\"}, {\"value\": 1250, \"componentId\": \"105\"}, {\"value\": \"GROSS - SUM(Fixed Earnings)\", \"componentId\": \"106\"}, {\"value\": 0, \"componentId\": \"107\"}, {\"value\": \"\", \"componentId\": \"108\"}, {\"value\": \"MIN(BASIC, 15000) * 12%\", \"componentId\": \"109\"}], \"active\": true, \"clientId\": \"10:RECL\", \"annualCtc\": \"0\"}, {\"id\": 9202, \"name\": \"RECL Laptop - Tel 2000\", \"lines\": [{\"value\": \"GROSS * 50%\", \"componentId\": \"101\"}, {\"value\": \"BASIC * 40%\", \"componentId\": \"102\"}, {\"value\": 2000, \"componentId\": \"103\"}, {\"value\": \"ROUNDDOWN(BASIC * 8.33%)\", \"componentId\": \"104\"}, {\"value\": 1250, \"componentId\": \"105\"}, {\"value\": \"GROSS - SUM(Fixed Earnings)\", \"componentId\": \"106\"}, {\"value\": 2000, \"componentId\": \"107\"}, {\"value\": \"\", \"componentId\": \"108\"}, {\"value\": \"MIN(BASIC, 15000) * 12%\", \"componentId\": \"109\"}], \"active\": true, \"clientId\": \"10:RECL\", \"annualCtc\": \"0\"}, {\"id\": 9203, \"name\": \"RECL Tel 1000\", \"lines\": [{\"value\": \"GROSS * 50%\", \"componentId\": \"101\"}, {\"value\": \"BASIC * 40%\", \"componentId\": \"102\"}, {\"value\": 1000, \"componentId\": \"103\"}, {\"value\": \"ROUNDDOWN(BASIC * 8.33%)\", \"componentId\": \"104\"}, {\"value\": 1250, \"componentId\": \"105\"}, {\"value\": \"GROSS - SUM(Fixed Earnings)\", \"componentId\": \"106\"}, {\"value\": 0, \"componentId\": \"107\"}, {\"value\": \"\", \"componentId\": \"108\"}, {\"value\": \"MIN(BASIC, 15000) * 12%\", \"componentId\": \"109\"}], \"active\": true, \"clientId\": \"10:RECL\", \"annualCtc\": \"0\"}, {\"id\": 9204, \"name\": \"RECL Tel 4000\", \"lines\": [{\"value\": \"GROSS * 50%\", \"componentId\": \"101\"}, {\"value\": \"BASIC * 40%\", \"componentId\": \"102\"}, {\"value\": 4000, \"componentId\": \"103\"}, {\"value\": \"ROUNDDOWN(BASIC * 8.33%)\", \"componentId\": \"104\"}, {\"value\": 1250, \"componentId\": \"105\"}, {\"value\": \"GROSS - SUM(Fixed Earnings)\", \"componentId\": \"106\"}, {\"value\": 0, \"componentId\": \"107\"}, {\"value\": \"\", \"componentId\": \"108\"}, {\"value\": \"MIN(BASIC, 15000) * 12%\", \"componentId\": \"109\"}], \"active\": true, \"clientId\": \"10:RECL\", \"annualCtc\": \"0\"}, {\"id\": 9205, \"name\": \"RECL MTS / No fixed allowance\", \"lines\": [{\"value\": \"GROSS * 50%\", \"componentId\": \"101\"}, {\"value\": \"BASIC * 40%\", \"componentId\": \"102\"}, {\"value\": 0, \"componentId\": \"103\"}, {\"value\": \"ROUNDDOWN(BASIC * 8.33%)\", \"componentId\": \"104\"}, {\"value\": 0, \"componentId\": \"105\"}, {\"value\": \"GROSS - SUM(Fixed Earnings)\", \"componentId\": \"106\"}, {\"value\": 0, \"componentId\": \"107\"}, {\"value\": \"\", \"componentId\": \"108\"}, {\"value\": \"MIN(BASIC, 15000) * 12%\", \"componentId\": \"109\"}], \"active\": true, \"clientId\": \"10:RECL\", \"annualCtc\": \"0\"}]}','2026-06-28 07:50:43');
/*!40000 ALTER TABLE `payrollsetups` ENABLE KEYS */;

--
-- Table structure for table `payrunemployeelines`
--

DROP TABLE IF EXISTS `payrunemployeelines`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `payrunemployeelines` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `PayRunEmployeeId` int NOT NULL,
  `PayRunId` int NOT NULL,
  `EmployeeId` int NOT NULL,
  `ComponentCode` varchar(80) NOT NULL,
  `Name` varchar(180) NOT NULL DEFAULT '',
  `Category` varchar(80) NOT NULL DEFAULT '',
  `MonthlyAmount` decimal(18,4) NOT NULL DEFAULT '0.0000',
  `Amount` decimal(18,4) NOT NULL DEFAULT '0.0000',
  `ProRata` tinyint(1) NOT NULL DEFAULT '0',
  `SortOrder` int NOT NULL DEFAULT '0',
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_PayRunEmployeeLines_Row_Component` (`PayRunEmployeeId`,`ComponentCode`,`SortOrder`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `payrunemployeelines`
--

/*!40000 ALTER TABLE `payrunemployeelines` DISABLE KEYS */;
/*!40000 ALTER TABLE `payrunemployeelines` ENABLE KEYS */;

--
-- Table structure for table `payrunemployees`
--

DROP TABLE IF EXISTS `payrunemployees`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `payrunemployees` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `PayRunId` int NOT NULL,
  `EmployeeId` int NOT NULL,
  `EmployeeCode` varchar(50) NOT NULL,
  `EmployeeName` varchar(250) NOT NULL,
  `Department` varchar(100) DEFAULT NULL,
  `PresentDays` int NOT NULL,
  `PayableDays` int NOT NULL,
  `MonthlyGross` decimal(18,2) NOT NULL DEFAULT '0.00',
  `GrossPay` decimal(18,2) NOT NULL DEFAULT '0.00',
  `StatutoryDeductions` decimal(18,2) NOT NULL DEFAULT '0.00',
  `OneTimeEarnings` decimal(18,2) NOT NULL DEFAULT '0.00',
  `OneTimeDeductions` decimal(18,2) NOT NULL DEFAULT '0.00',
  `NetPay` decimal(18,2) NOT NULL DEFAULT '0.00',
  `IsSkipped` tinyint(1) NOT NULL DEFAULT '0',
  `PaymentStatus` varchar(30) NOT NULL DEFAULT 'Pending',
  `DetailsJson` json NOT NULL,
  `PaymentDate` date DEFAULT NULL,
  `ClientId` int NOT NULL DEFAULT '0',
  `ClientName` varchar(250) DEFAULT NULL,
  `ManualTds` decimal(18,2) NOT NULL DEFAULT '0.00',
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_PayRunEmployees_Run_Employee` (`PayRunId`,`EmployeeId`),
  CONSTRAINT `FK_PayRunEmployees_PayRuns` FOREIGN KEY (`PayRunId`) REFERENCES `payruns` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=135 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `payrunemployees`
--

/*!40000 ALTER TABLE `payrunemployees` DISABLE KEYS */;
INSERT INTO `payrunemployees` VALUES (132,9,595,'ACME001','Amit Verma','Engineering',29,29,0.00,0.00,0.00,0.00,0.00,0.00,0,'Pending','[]',NULL,7,'Acme Technologies',0.00),(133,9,600,'NORTH001','Ananya Rao','Engineering',30,0,0.00,0.00,0.00,0.00,0.00,0.00,1,'Pending','[]',NULL,7,'Acme Technologies',0.00),(134,9,676,'APPC0023','Rishav Mishra','Engineering',30,0,0.00,0.00,0.00,0.00,0.00,0.00,1,'Pending','[]',NULL,7,'Acme Technologies',0.00);
/*!40000 ALTER TABLE `payrunemployees` ENABLE KEYS */;

--
-- Table structure for table `payruns`
--

DROP TABLE IF EXISTS `payruns`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `payruns` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `PayPeriod` varchar(7) NOT NULL,
  `PayDate` date NOT NULL,
  `TotalWorkingDays` int NOT NULL,
  `Status` varchar(30) NOT NULL DEFAULT 'Draft',
  `PayrollCost` decimal(18,2) NOT NULL DEFAULT '0.00',
  `NetPay` decimal(18,2) NOT NULL DEFAULT '0.00',
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `ClientId` int NOT NULL DEFAULT '0',
  `ClientName` varchar(250) DEFAULT NULL,
  `RunCode` varchar(40) NOT NULL DEFAULT 'REGULAR',
  `RunType` varchar(30) NOT NULL DEFAULT 'Regular',
  `RunName` varchar(120) NOT NULL DEFAULT '',
  `Reason` varchar(500) NOT NULL DEFAULT '',
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_PayRuns_Client_Period_Code` (`ClientId`,`PayPeriod`,`RunCode`)
) ENGINE=InnoDB AUTO_INCREMENT=10 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `payruns`
--

/*!40000 ALTER TABLE `payruns` DISABLE KEYS */;
INSERT INTO `payruns` VALUES (9,'2026-06','2026-06-28',30,'Draft',0.00,0.00,'2026-06-26 17:44:54','2026-06-26 17:44:54',7,'Acme Technologies','REGULAR','Regular','Regular payroll','');
/*!40000 ALTER TABLE `payruns` ENABLE KEYS */;

--
-- Table structure for table `paysliptemplates`
--

DROP TABLE IF EXISTS `paysliptemplates`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `paysliptemplates` (
  `Id` bigint NOT NULL,
  `ClientId` int NOT NULL DEFAULT '0',
  `ClientRef` varchar(200) NOT NULL DEFAULT '',
  `Name` varchar(200) NOT NULL DEFAULT '',
  `Theme` varchar(80) NOT NULL DEFAULT 'Classic',
  `ShowLogo` tinyint(1) NOT NULL DEFAULT '1',
  `ShowClient` tinyint(1) NOT NULL DEFAULT '1',
  `ShowYtd` tinyint(1) NOT NULL DEFAULT '1',
  `ShowBank` tinyint(1) NOT NULL DEFAULT '1',
  `Note` varchar(1000) NOT NULL DEFAULT '',
  `Active` tinyint(1) NOT NULL DEFAULT '1',
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `paysliptemplates`
--

/*!40000 ALTER TABLE `paysliptemplates` DISABLE KEYS */;
INSERT INTO `paysliptemplates` VALUES (301,1,'1:Acme Technologies','Acme Classic Payslip','Classic',1,1,1,1,'This is a system generated payslip.',1,'2026-06-28 09:23:38');
/*!40000 ALTER TABLE `paysliptemplates` ENABLE KEYS */;

--
-- Table structure for table `salarycomponents`
--

DROP TABLE IF EXISTS `salarycomponents`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `salarycomponents` (
  `Id` bigint NOT NULL,
  `Code` varchar(80) NOT NULL,
  `ComponentType` varchar(120) NOT NULL DEFAULT '',
  `Category` varchar(60) NOT NULL DEFAULT 'Earning',
  `Name` varchar(160) NOT NULL,
  `PayType` varchar(60) NOT NULL DEFAULT 'Fixed Pay',
  `CalculationType` varchar(80) NOT NULL DEFAULT 'Fixed Amount',
  `ValueText` varchar(500) NOT NULL DEFAULT '',
  `Formula` varchar(1000) NOT NULL DEFAULT '',
  `BaseComponent` varchar(80) NOT NULL DEFAULT '',
  `Taxable` tinyint(1) NOT NULL DEFAULT '1',
  `Ctc` tinyint(1) NOT NULL DEFAULT '1',
  `ProRata` tinyint(1) NOT NULL DEFAULT '1',
  `Fbp` tinyint(1) NOT NULL DEFAULT '0',
  `RestrictFbp` tinyint(1) NOT NULL DEFAULT '0',
  `Epf` varchar(40) NOT NULL DEFAULT 'Never',
  `Esi` tinyint(1) NOT NULL DEFAULT '0',
  `Recurring` tinyint(1) NOT NULL DEFAULT '1',
  `Scheduled` tinyint(1) NOT NULL DEFAULT '0',
  `InvestmentType` varchar(120) NOT NULL DEFAULT '',
  `CorrectionOf` varchar(120) NOT NULL DEFAULT '',
  `Active` tinyint(1) NOT NULL DEFAULT '1',
  `Priority` int NOT NULL DEFAULT '999',
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_SalaryComponents_Code` (`Code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `salarycomponents`
--

/*!40000 ALTER TABLE `salarycomponents` DISABLE KEYS */;
INSERT INTO `salarycomponents` VALUES (101,'BASIC','Basic','Earning','Basic','Fixed Pay','Formula','','GROSS * 50%','GROSS',1,1,1,0,0,'Always',1,1,0,'','',1,10,'2026-06-28 09:23:37'),(102,'HRA','House Rent Allowance','Earning','HRA','Fixed Pay','Formula','','BASIC * 40%','BASIC',1,1,1,0,0,'Never',1,1,0,'','',1,20,'2026-06-28 09:23:37'),(103,'TEL_ALLOW','Telephone','Earning','Telephonic Allowance','Fixed Pay','Fixed Amount','2000','','',1,1,1,0,0,'Never',1,1,0,'','',1,30,'2026-06-28 09:23:38'),(104,'STAT_BONUS','Bonus','Earning','Statutory Bonus','Fixed Pay','Formula','','ROUNDDOWN(BASIC * 8.33%)','BASIC',1,1,1,0,0,'Never',1,1,0,'','',1,40,'2026-06-28 09:23:38'),(105,'MED_ALLOW','Medical Allowance','Earning','Medical Allowance','Fixed Pay','Fixed Amount','1250','','',1,1,1,0,0,'Never',1,1,0,'','',1,50,'2026-06-28 09:23:38'),(106,'OTHER_ALLOW','Custom Allowance','Earning','Other Allowance','Fixed Pay','Residual / Balancing','','GROSS - SUM(Fixed Earnings)','GROSS',1,1,1,0,0,'Never',1,1,0,'','',1,60,'2026-06-28 09:23:38'),(107,'LAPTOP_ALLOW','Custom Allowance','Earning','Laptop Allowance','Fixed Pay','Fixed Amount','2000','','',1,1,1,0,0,'Never',1,1,0,'','',1,70,'2026-06-28 09:23:38'),(108,'TA_DA','Custom Allowance','Earning','TA/DA','Variable Pay','Manual / Variable','','','',1,0,1,0,0,'Never',1,0,1,'','',1,80,'2026-06-28 09:23:38'),(109,'PF','Provident Fund','Deduction','Provident Fund','Fixed Pay','Formula','','MIN(BASIC, 15000) * 12%','BASIC',0,1,1,0,0,'Never',0,1,0,'','',1,110,'2026-06-28 09:23:38'),(110,'ESIC','Employee State Insurance','Deduction','Employee ESIC','Fixed Pay','Manual / Variable','','','',0,0,0,0,0,'Never',0,1,0,'','',1,120,'2026-06-28 09:36:20'),(111,'PT_LWF_WC','Professional Tax / LWF / Workmen Comp','Deduction','PT / LWF / Workmen Comp','Fixed Pay','Manual / Variable','','','',0,0,0,0,0,'Never',0,1,0,'','',1,130,'2026-06-28 09:36:20'),(112,'TDS','Tax Deducted at Source','Deduction','TDS','Fixed Pay','Manual / Variable','','','',0,0,0,0,0,'Never',0,1,0,'','',1,140,'2026-06-28 09:36:20'),(113,'RECOVERY','Recovery','Deduction','Recovery','Variable Pay','Manual / Variable','','','',0,0,0,0,0,'Never',0,0,1,'','',1,150,'2026-06-28 09:36:20');
/*!40000 ALTER TABLE `salarycomponents` ENABLE KEYS */;

--
-- Table structure for table `salarystructurelines`
--

DROP TABLE IF EXISTS `salarystructurelines`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `salarystructurelines` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `StructureId` bigint NOT NULL,
  `ComponentId` varchar(80) NOT NULL,
  `ValueText` varchar(1000) NOT NULL DEFAULT '',
  `SortOrder` int NOT NULL DEFAULT '0',
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_SalaryStructureLines_Structure_Component` (`StructureId`,`ComponentId`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `salarystructurelines`
--

/*!40000 ALTER TABLE `salarystructurelines` DISABLE KEYS */;
/*!40000 ALTER TABLE `salarystructurelines` ENABLE KEYS */;

--
-- Table structure for table `salarystructures`
--

DROP TABLE IF EXISTS `salarystructures`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `salarystructures` (
  `Id` bigint NOT NULL,
  `ClientId` int NOT NULL DEFAULT '0',
  `ClientRef` varchar(200) NOT NULL DEFAULT '',
  `Name` varchar(200) NOT NULL DEFAULT '',
  `AnnualCtc` decimal(18,2) NOT NULL DEFAULT '0.00',
  `Active` tinyint(1) NOT NULL DEFAULT '1',
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `salarystructures`
--

/*!40000 ALTER TABLE `salarystructures` DISABLE KEYS */;
/*!40000 ALTER TABLE `salarystructures` ENABLE KEYS */;

--
-- Table structure for table `tax_activity_windows`
--

DROP TABLE IF EXISTS `tax_activity_windows`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_activity_windows` (
  `id` int NOT NULL AUTO_INCREMENT,
  `client_setting_id` int DEFAULT NULL,
  `client_id` int NOT NULL,
  `financial_year` varchar(10) NOT NULL,
  `financial_year_id` int DEFAULT NULL,
  `activity_code` varchar(40) NOT NULL,
  `is_open` tinyint(1) NOT NULL DEFAULT '0',
  `start_date` date DEFAULT NULL,
  `end_date` date DEFAULT NULL,
  `cutoff_date` date DEFAULT NULL,
  `processing_month` varchar(7) NOT NULL DEFAULT '',
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_tax_activity_client_fy` (`client_id`,`financial_year`,`activity_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_activity_windows`
--

/*!40000 ALTER TABLE `tax_activity_windows` DISABLE KEYS */;
/*!40000 ALTER TABLE `tax_activity_windows` ENABLE KEYS */;

--
-- Table structure for table `tax_client_settings`
--

DROP TABLE IF EXISTS `tax_client_settings`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_client_settings` (
  `id` int NOT NULL AUTO_INCREMENT,
  `client_id` int NOT NULL,
  `enabled` tinyint(1) NOT NULL DEFAULT '1',
  `financial_year` varchar(10) NOT NULL,
  `financial_year_id` int DEFAULT NULL,
  `default_regime` varchar(10) NOT NULL DEFAULT 'New',
  `allow_employee_regime_selection` tinyint(1) NOT NULL DEFAULT '1',
  `regime_selection_window_open` tinyint(1) NOT NULL DEFAULT '0',
  `regime_selection_cutoff` date DEFAULT NULL,
  `allow_declarations` tinyint(1) NOT NULL DEFAULT '1',
  `planned_declaration_window_open` tinyint(1) NOT NULL DEFAULT '0',
  `actual_declaration_window_open` tinyint(1) NOT NULL DEFAULT '0',
  `declaration_window_start` date DEFAULT NULL,
  `declaration_window_end` date DEFAULT NULL,
  `planned_declaration_start` date DEFAULT NULL,
  `planned_declaration_end` date DEFAULT NULL,
  `actual_declaration_start` date DEFAULT NULL,
  `actual_declaration_end` date DEFAULT NULL,
  `poi_processing_month` varchar(7) NOT NULL DEFAULT '',
  `reminder_emails_enabled` tinyint(1) NOT NULL DEFAULT '1',
  `reminder_frequency` varchar(30) NOT NULL DEFAULT 'Weekly',
  `reminder_before_lock_days` int NOT NULL DEFAULT '7',
  `require_proof_upload` tinyint(1) NOT NULL DEFAULT '1',
  `require_approval` tinyint(1) NOT NULL DEFAULT '1',
  `tax_deduction_component_code` varchar(40) NOT NULL DEFAULT 'TDS',
  `project_monthly_tds` tinyint(1) NOT NULL DEFAULT '1',
  `lock_after_approval` tinyint(1) NOT NULL DEFAULT '1',
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_tax_client_fy` (`client_id`,`financial_year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_client_settings`
--

/*!40000 ALTER TABLE `tax_client_settings` DISABLE KEYS */;
/*!40000 ALTER TABLE `tax_client_settings` ENABLE KEYS */;

--
-- Table structure for table `tax_computation_snapshots`
--

DROP TABLE IF EXISTS `tax_computation_snapshots`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_computation_snapshots` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `employee_id` int NOT NULL,
  `client_id` int NOT NULL,
  `financial_year` varchar(10) NOT NULL,
  `pay_period` varchar(7) NOT NULL,
  `financial_year_id` int DEFAULT NULL,
  `rule_version_id` int NOT NULL,
  `regime_id` int DEFAULT NULL,
  `regime` varchar(20) NOT NULL,
  `gross_salary` decimal(14,2) NOT NULL,
  `exemptions_amount` decimal(14,2) NOT NULL DEFAULT '0.00',
  `deductions_amount` decimal(14,2) NOT NULL DEFAULT '0.00',
  `taxable_income` decimal(14,2) NOT NULL,
  `tax_before_rebate` decimal(14,2) NOT NULL DEFAULT '0.00',
  `rebate_amount` decimal(14,2) NOT NULL DEFAULT '0.00',
  `tax_after_rebate` decimal(14,2) NOT NULL DEFAULT '0.00',
  `surcharge_amount` decimal(14,2) NOT NULL DEFAULT '0.00',
  `cess_amount` decimal(14,2) NOT NULL DEFAULT '0.00',
  `total_annual_tax` decimal(14,2) NOT NULL DEFAULT '0.00',
  `tds_deducted_till_date` decimal(14,2) NOT NULL DEFAULT '0.00',
  `remaining_tax` decimal(14,2) NOT NULL DEFAULT '0.00',
  `annual_tax` decimal(14,2) NOT NULL,
  `monthly_tds` decimal(14,2) NOT NULL,
  `snapshot_json` json NOT NULL,
  `rule_breakup_json` json DEFAULT NULL,
  `declaration_json` json DEFAULT NULL,
  `proof_json` json DEFAULT NULL,
  `calculated_by` int DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_computation_snapshots`
--

/*!40000 ALTER TABLE `tax_computation_snapshots` DISABLE KEYS */;
/*!40000 ALTER TABLE `tax_computation_snapshots` ENABLE KEYS */;

--
-- Table structure for table `tax_declaration_sections`
--

DROP TABLE IF EXISTS `tax_declaration_sections`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_declaration_sections` (
  `id` int NOT NULL AUTO_INCREMENT,
  `rule_version_id` int NOT NULL DEFAULT '0',
  `regime_id` int NOT NULL DEFAULT '0',
  `financial_year` varchar(10) NOT NULL DEFAULT '',
  `code` varchar(40) NOT NULL,
  `name` varchar(180) NOT NULL,
  `category` varchar(80) NOT NULL DEFAULT 'Investment',
  `regime` varchar(10) NOT NULL DEFAULT 'Old',
  `limit_amount` decimal(14,2) DEFAULT NULL,
  `proof_required` tinyint(1) NOT NULL DEFAULT '1',
  `requires_approval` tinyint(1) NOT NULL DEFAULT '1',
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `source` varchar(250) NOT NULL DEFAULT '',
  `notes` varchar(1000) NOT NULL DEFAULT '',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_tax_section_code_fy` (`financial_year`,`code`)
) ENGINE=InnoDB AUTO_INCREMENT=38 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_declaration_sections`
--

/*!40000 ALTER TABLE `tax_declaration_sections` DISABLE KEYS */;
INSERT INTO `tax_declaration_sections` VALUES (1,1,0,'2026-27','80C','Section 80C Investments','Investment','Old',150000.00,1,1,1,'Income Tax Act','PF, ELSS, life insurance and eligible investments','2026-06-27 19:35:21','2026-06-27 19:35:21'),(2,1,0,'2026-27','80CCD1B','Additional NPS Contribution','Investment','Old',50000.00,1,1,1,'Income Tax Act','Additional NPS deduction','2026-06-27 19:35:21','2026-06-27 19:35:21'),(3,1,0,'2026-27','80D','Medical Insurance Premium','Insurance','Old',25000.00,1,1,1,'Income Tax Act','Health insurance deduction','2026-06-27 19:35:21','2026-06-27 19:35:21'),(4,1,0,'2026-27','HRA','House Rent Allowance','Exemption','Old',NULL,1,1,1,'Income Tax Act / Rules','HRA exemption using configured HRA rule','2026-06-27 19:35:21','2026-06-27 19:35:21'),(5,1,0,'2026-27','24B','Home Loan Interest','Housing','Old',200000.00,1,1,1,'Income Tax Act','Self occupied property interest','2026-06-27 19:35:21','2026-06-27 19:35:21'),(6,1,0,'2026-27','80E','Education Loan Interest','Loan','Old',NULL,1,1,1,'Income Tax Act','Education loan interest','2026-06-27 19:35:21','2026-06-27 19:35:21'),(7,1,0,'2026-27','80G','Donations','Donation','Old',NULL,1,1,1,'Income Tax Act','Eligible donations','2026-06-27 19:35:21','2026-06-27 19:35:21');
/*!40000 ALTER TABLE `tax_declaration_sections` ENABLE KEYS */;

--
-- Table structure for table `tax_deduction_options`
--

DROP TABLE IF EXISTS `tax_deduction_options`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_deduction_options` (
  `id` int NOT NULL AUTO_INCREMENT,
  `section_id` int NOT NULL,
  `financial_year` varchar(10) NOT NULL,
  `code` varchar(60) NOT NULL,
  `name` varchar(180) NOT NULL,
  `limit_amount` decimal(14,2) DEFAULT NULL,
  `proof_required` tinyint(1) NOT NULL DEFAULT '1',
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `notes` varchar(1000) NOT NULL DEFAULT '',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_tax_deduction_option` (`financial_year`,`code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_deduction_options`
--

/*!40000 ALTER TABLE `tax_deduction_options` DISABLE KEYS */;
/*!40000 ALTER TABLE `tax_deduction_options` ENABLE KEYS */;

--
-- Table structure for table `tax_exemption_rules`
--

DROP TABLE IF EXISTS `tax_exemption_rules`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_exemption_rules` (
  `id` int NOT NULL AUTO_INCREMENT,
  `rule_version_id` int NOT NULL DEFAULT '0',
  `financial_year` varchar(10) NOT NULL,
  `code` varchar(60) NOT NULL,
  `name` varchar(180) NOT NULL,
  `formula_json` json DEFAULT NULL,
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `source` varchar(250) NOT NULL DEFAULT '',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_exemption_rules`
--

/*!40000 ALTER TABLE `tax_exemption_rules` DISABLE KEYS */;
/*!40000 ALTER TABLE `tax_exemption_rules` ENABLE KEYS */;

--
-- Table structure for table `tax_final_adjustments`
--

DROP TABLE IF EXISTS `tax_final_adjustments`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_final_adjustments` (
  `id` int NOT NULL AUTO_INCREMENT,
  `rule_version_id` int NOT NULL DEFAULT '0',
  `financial_year` varchar(10) NOT NULL DEFAULT '',
  `label` varchar(120) NOT NULL,
  `value_type` varchar(20) NOT NULL DEFAULT 'Percent',
  `value` decimal(14,4) NOT NULL DEFAULT '0.0000',
  `apply_order` int NOT NULL DEFAULT '100',
  `rule_type` varchar(40) NOT NULL DEFAULT 'FinalAdjustment',
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `source` varchar(250) NOT NULL DEFAULT '',
  `notes` varchar(1000) NOT NULL DEFAULT '',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_final_adjustments`
--

/*!40000 ALTER TABLE `tax_final_adjustments` DISABLE KEYS */;
INSERT INTO `tax_final_adjustments` VALUES (1,1,'2026-27','Health & Education Cess','Percent',4.0000,100,'FinalAdjustment',1,'Finance Act / Income Tax Department','Configurable final tax adjustment','2026-06-27 19:35:21','2026-06-27 19:35:21');
/*!40000 ALTER TABLE `tax_final_adjustments` ENABLE KEYS */;

--
-- Table structure for table `tax_financial_years`
--

DROP TABLE IF EXISTS `tax_financial_years`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_financial_years` (
  `id` int NOT NULL AUTO_INCREMENT,
  `code` varchar(10) NOT NULL,
  `start_date` date NOT NULL,
  `end_date` date NOT NULL,
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `notes` varchar(1000) NOT NULL DEFAULT '',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_tax_financial_year_code` (`code`)
) ENGINE=InnoDB AUTO_INCREMENT=8 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_financial_years`
--

/*!40000 ALTER TABLE `tax_financial_years` DISABLE KEYS */;
INSERT INTO `tax_financial_years` VALUES (1,'2026-27','2026-04-01','2027-03-31',1,'Seeded statutory financial year','2026-06-27 19:35:20','2026-06-27 19:35:20');
/*!40000 ALTER TABLE `tax_financial_years` ENABLE KEYS */;

--
-- Table structure for table `tax_hra_rules`
--

DROP TABLE IF EXISTS `tax_hra_rules`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_hra_rules` (
  `id` int NOT NULL AUTO_INCREMENT,
  `rule_version_id` int NOT NULL DEFAULT '0',
  `regime_id` int NOT NULL DEFAULT '0',
  `financial_year` varchar(10) NOT NULL,
  `is_applicable` tinyint(1) NOT NULL DEFAULT '1',
  `metro_salary_percent` decimal(6,2) NOT NULL DEFAULT '50.00',
  `non_metro_salary_percent` decimal(6,2) NOT NULL DEFAULT '40.00',
  `rent_minus_basic_percent` decimal(6,2) NOT NULL DEFAULT '10.00',
  `formula_type` varchar(40) NOT NULL DEFAULT 'LeastOf',
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `source` varchar(250) NOT NULL DEFAULT '',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_hra_rules`
--

/*!40000 ALTER TABLE `tax_hra_rules` DISABLE KEYS */;
INSERT INTO `tax_hra_rules` VALUES (1,1,0,'2026-27',1,50.00,40.00,10.00,'LeastOf',1,'Income Tax Act / Income Tax Rules','2026-06-27 19:35:21','2026-06-27 19:35:21');
/*!40000 ALTER TABLE `tax_hra_rules` ENABLE KEYS */;

--
-- Table structure for table `tax_rebate_rules`
--

DROP TABLE IF EXISTS `tax_rebate_rules`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_rebate_rules` (
  `id` int NOT NULL AUTO_INCREMENT,
  `rule_version_id` int NOT NULL DEFAULT '0',
  `financial_year` varchar(10) NOT NULL,
  `regime` varchar(20) NOT NULL DEFAULT 'Both',
  `income_limit` decimal(14,2) NOT NULL DEFAULT '0.00',
  `rebate_amount` decimal(14,2) NOT NULL DEFAULT '0.00',
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `source` varchar(250) NOT NULL DEFAULT '',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_rebate_rules`
--

/*!40000 ALTER TABLE `tax_rebate_rules` DISABLE KEYS */;
INSERT INTO `tax_rebate_rules` VALUES (1,1,'2026-27','Old',500000.00,12500.00,1,'Finance Act / Income Tax Department','2026-06-27 19:35:21','2026-06-27 19:35:21'),(2,1,'2026-27','New',700000.00,25000.00,1,'Finance Act / Income Tax Department','2026-06-27 19:35:21','2026-06-27 19:35:21');
/*!40000 ALTER TABLE `tax_rebate_rules` ENABLE KEYS */;

--
-- Table structure for table `tax_regimes`
--

DROP TABLE IF EXISTS `tax_regimes`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_regimes` (
  `id` int NOT NULL AUTO_INCREMENT,
  `rule_version_id` int NOT NULL DEFAULT '0',
  `financial_year` varchar(10) NOT NULL,
  `code` varchar(20) NOT NULL,
  `name` varchar(120) NOT NULL,
  `is_default` tinyint(1) NOT NULL DEFAULT '0',
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `notes` varchar(1000) NOT NULL DEFAULT '',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_tax_regime_fy_code` (`financial_year`,`code`)
) ENGINE=InnoDB AUTO_INCREMENT=13 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_regimes`
--

/*!40000 ALTER TABLE `tax_regimes` DISABLE KEYS */;
INSERT INTO `tax_regimes` VALUES (1,1,'2026-27','Old','Old Regime',0,1,'Deductions and exemptions allowed','2026-06-27 19:35:21','2026-06-27 19:35:21'),(2,1,'2026-27','New','New Regime',1,1,'Default concessional regime','2026-06-27 19:35:21','2026-06-27 19:35:21');
/*!40000 ALTER TABLE `tax_regimes` ENABLE KEYS */;

--
-- Table structure for table `tax_rule_audit_logs`
--

DROP TABLE IF EXISTS `tax_rule_audit_logs`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_rule_audit_logs` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `entity_name` varchar(120) NOT NULL,
  `entity_id` bigint NOT NULL,
  `action` varchar(40) NOT NULL,
  `old_value_json` json DEFAULT NULL,
  `new_value_json` json DEFAULT NULL,
  `changed_by` int DEFAULT NULL,
  `changed_on` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `change_reason` varchar(500) NOT NULL DEFAULT '',
  `financial_year_id` int DEFAULT NULL,
  `tax_rule_version_id` int DEFAULT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_rule_audit_logs`
--

/*!40000 ALTER TABLE `tax_rule_audit_logs` DISABLE KEYS */;
/*!40000 ALTER TABLE `tax_rule_audit_logs` ENABLE KEYS */;

--
-- Table structure for table `tax_rule_source_references`
--

DROP TABLE IF EXISTS `tax_rule_source_references`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_rule_source_references` (
  `id` int NOT NULL AUTO_INCREMENT,
  `rule_version_id` int NOT NULL DEFAULT '0',
  `financial_year` varchar(10) NOT NULL,
  `source_type` varchar(80) NOT NULL,
  `title` varchar(250) NOT NULL,
  `url` varchar(500) NOT NULL DEFAULT '',
  `document_number` varchar(120) NOT NULL DEFAULT '',
  `published_date` date DEFAULT NULL,
  `effective_from` date DEFAULT NULL,
  `notes` varchar(1000) NOT NULL DEFAULT '',
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_rule_source_references`
--

/*!40000 ALTER TABLE `tax_rule_source_references` DISABLE KEYS */;
INSERT INTO `tax_rule_source_references` VALUES (1,1,'2026-27','Government','Income Tax Department India','https://www.incometax.gov.in/','',NULL,NULL,'Statutory rule traceability source',1,'2026-06-27 19:35:21');
/*!40000 ALTER TABLE `tax_rule_source_references` ENABLE KEYS */;

--
-- Table structure for table `tax_rule_versions`
--

DROP TABLE IF EXISTS `tax_rule_versions`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_rule_versions` (
  `id` int NOT NULL AUTO_INCREMENT,
  `financial_year_id` int DEFAULT NULL,
  `financial_year` varchar(10) NOT NULL,
  `version_number` varchar(30) NOT NULL DEFAULT '1.0',
  `version_name` varchar(120) NOT NULL DEFAULT '',
  `effective_from` date NOT NULL,
  `effective_to` date DEFAULT NULL,
  `is_published` tinyint(1) NOT NULL DEFAULT '1',
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `source_reference_id` int DEFAULT NULL,
  `source` varchar(250) NOT NULL DEFAULT '',
  `notes` varchar(1000) NOT NULL DEFAULT '',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_tax_rule_version` (`financial_year`,`version_number`)
) ENGINE=InnoDB AUTO_INCREMENT=8 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_rule_versions`
--

/*!40000 ALTER TABLE `tax_rule_versions` DISABLE KEYS */;
INSERT INTO `tax_rule_versions` VALUES (1,1,'2026-27','1.0','','2026-04-01','2027-03-31',1,1,NULL,'Finance Act / CBDT / Income Tax Department','Initial statutory seed','2026-06-27 19:35:20','2026-06-27 19:35:21');
/*!40000 ALTER TABLE `tax_rule_versions` ENABLE KEYS */;

--
-- Table structure for table `tax_slabs`
--

DROP TABLE IF EXISTS `tax_slabs`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_slabs` (
  `id` int NOT NULL AUTO_INCREMENT,
  `rule_version_id` int NOT NULL DEFAULT '0',
  `regime_id` int NOT NULL DEFAULT '0',
  `financial_year` varchar(10) NOT NULL DEFAULT '',
  `regime` varchar(10) NOT NULL,
  `income_from` decimal(14,2) NOT NULL DEFAULT '0.00',
  `income_to` decimal(14,2) DEFAULT NULL,
  `rate_percent` decimal(6,2) NOT NULL DEFAULT '0.00',
  `effective_from` date NOT NULL,
  `effective_to` date DEFAULT NULL,
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `source` varchar(250) NOT NULL DEFAULT '',
  `notes` varchar(1000) NOT NULL DEFAULT '',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=11 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_slabs`
--

/*!40000 ALTER TABLE `tax_slabs` DISABLE KEYS */;
INSERT INTO `tax_slabs` VALUES (1,1,0,'2026-27','Old',0.00,250000.00,0.00,'2026-04-01','2027-03-31',1,'Finance Act / Income Tax Department','Initial statutory seed','2026-06-27 19:35:21','2026-06-27 19:35:21'),(2,1,0,'2026-27','Old',250000.00,500000.00,5.00,'2026-04-01','2027-03-31',1,'Finance Act / Income Tax Department','Initial statutory seed','2026-06-27 19:35:21','2026-06-27 19:35:21'),(3,1,0,'2026-27','Old',500000.00,1000000.00,20.00,'2026-04-01','2027-03-31',1,'Finance Act / Income Tax Department','Initial statutory seed','2026-06-27 19:35:21','2026-06-27 19:35:21'),(4,1,0,'2026-27','Old',1000000.00,NULL,30.00,'2026-04-01','2027-03-31',1,'Finance Act / Income Tax Department','Initial statutory seed','2026-06-27 19:35:21','2026-06-27 19:35:21'),(5,1,0,'2026-27','New',0.00,300000.00,0.00,'2026-04-01','2027-03-31',1,'Finance Act / Income Tax Department','Initial statutory seed','2026-06-27 19:35:21','2026-06-27 19:35:21'),(6,1,0,'2026-27','New',300000.00,600000.00,5.00,'2026-04-01','2027-03-31',1,'Finance Act / Income Tax Department','Initial statutory seed','2026-06-27 19:35:21','2026-06-27 19:35:21'),(7,1,0,'2026-27','New',600000.00,900000.00,10.00,'2026-04-01','2027-03-31',1,'Finance Act / Income Tax Department','Initial statutory seed','2026-06-27 19:35:21','2026-06-27 19:35:21'),(8,1,0,'2026-27','New',900000.00,1200000.00,15.00,'2026-04-01','2027-03-31',1,'Finance Act / Income Tax Department','Initial statutory seed','2026-06-27 19:35:21','2026-06-27 19:35:21'),(9,1,0,'2026-27','New',1200000.00,1500000.00,20.00,'2026-04-01','2027-03-31',1,'Finance Act / Income Tax Department','Initial statutory seed','2026-06-27 19:35:21','2026-06-27 19:35:21'),(10,1,0,'2026-27','New',1500000.00,NULL,30.00,'2026-04-01','2027-03-31',1,'Finance Act / Income Tax Department','Initial statutory seed','2026-06-27 19:35:21','2026-06-27 19:35:21');
/*!40000 ALTER TABLE `tax_slabs` ENABLE KEYS */;

--
-- Table structure for table `tax_standard_deductions`
--

DROP TABLE IF EXISTS `tax_standard_deductions`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_standard_deductions` (
  `id` int NOT NULL AUTO_INCREMENT,
  `rule_version_id` int NOT NULL DEFAULT '0',
  `financial_year` varchar(10) NOT NULL,
  `regime` varchar(20) NOT NULL DEFAULT 'Both',
  `amount` decimal(14,2) NOT NULL DEFAULT '0.00',
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `source` varchar(250) NOT NULL DEFAULT '',
  `notes` varchar(1000) NOT NULL DEFAULT '',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_standard_deductions`
--

/*!40000 ALTER TABLE `tax_standard_deductions` DISABLE KEYS */;
INSERT INTO `tax_standard_deductions` VALUES (1,1,'2026-27','Both',50000.00,1,'Finance Act / Income Tax Department','Seeded statutory standard deduction','2026-06-27 19:35:21','2026-06-27 19:35:21');
/*!40000 ALTER TABLE `tax_standard_deductions` ENABLE KEYS */;

--
-- Table structure for table `tax_surcharges`
--

DROP TABLE IF EXISTS `tax_surcharges`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `tax_surcharges` (
  `id` int NOT NULL AUTO_INCREMENT,
  `rule_version_id` int NOT NULL DEFAULT '0',
  `regime_id` int NOT NULL DEFAULT '0',
  `financial_year` varchar(10) NOT NULL DEFAULT '',
  `income_from` decimal(14,2) NOT NULL DEFAULT '0.00',
  `income_to` decimal(14,2) DEFAULT NULL,
  `surcharge_percent` decimal(6,2) NOT NULL DEFAULT '0.00',
  `active` tinyint(1) NOT NULL DEFAULT '1',
  `source` varchar(250) NOT NULL DEFAULT '',
  `notes` varchar(1000) NOT NULL DEFAULT '',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=6 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `tax_surcharges`
--

/*!40000 ALTER TABLE `tax_surcharges` DISABLE KEYS */;
INSERT INTO `tax_surcharges` VALUES (1,1,0,'2026-27',0.00,5000000.00,0.00,1,'Finance Act / Income Tax Department','Income based surcharge threshold','2026-06-27 19:35:21','2026-06-27 19:35:21'),(2,1,0,'2026-27',5000000.00,10000000.00,10.00,1,'Finance Act / Income Tax Department','Income based surcharge threshold','2026-06-27 19:35:21','2026-06-27 19:35:21'),(3,1,0,'2026-27',10000000.00,20000000.00,15.00,1,'Finance Act / Income Tax Department','Income based surcharge threshold','2026-06-27 19:35:21','2026-06-27 19:35:21'),(4,1,0,'2026-27',20000000.00,50000000.00,25.00,1,'Finance Act / Income Tax Department','Income based surcharge threshold','2026-06-27 19:35:21','2026-06-27 19:35:21'),(5,1,0,'2026-27',50000000.00,NULL,37.00,1,'Finance Act / Income Tax Department','Income based surcharge threshold','2026-06-27 19:35:21','2026-06-27 19:35:21');
/*!40000 ALTER TABLE `tax_surcharges` ENABLE KEYS */;

--
-- Table structure for table `workflowhistory`
--

DROP TABLE IF EXISTS `workflowhistory`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `workflowhistory` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `InstanceId` bigint NOT NULL,
  `TaskId` bigint DEFAULT NULL,
  `Action` varchar(30) NOT NULL,
  `ActorUserId` int NOT NULL,
  `Comment` varchar(1000) DEFAULT NULL,
  `CreatedAt` datetime DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=4 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `workflowhistory`
--

/*!40000 ALTER TABLE `workflowhistory` DISABLE KEYS */;
/*!40000 ALTER TABLE `workflowhistory` ENABLE KEYS */;

--
-- Table structure for table `workflowinstances`
--

DROP TABLE IF EXISTS `workflowinstances`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `workflowinstances` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `WorkflowId` int NOT NULL,
  `ResourceType` varchar(100) NOT NULL,
  `ResourceId` varchar(120) NOT NULL,
  `RequestorUserId` int NOT NULL,
  `PayloadJson` json NOT NULL,
  `Status` varchar(30) NOT NULL DEFAULT 'Pending',
  `CreatedAt` datetime DEFAULT CURRENT_TIMESTAMP,
  `CompletedAt` datetime DEFAULT NULL,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `workflowinstances`
--

/*!40000 ALTER TABLE `workflowinstances` DISABLE KEYS */;
/*!40000 ALTER TABLE `workflowinstances` ENABLE KEYS */;

--
-- Table structure for table `workflowmasters`
--

DROP TABLE IF EXISTS `workflowmasters`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `workflowmasters` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ClientId` int DEFAULT NULL,
  `Code` varchar(80) NOT NULL,
  `Name` varchar(180) NOT NULL,
  `ResourceType` varchar(100) NOT NULL,
  `IsActive` tinyint(1) NOT NULL DEFAULT '1',
  `CreatedAt` datetime DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=6 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `workflowmasters`
--

/*!40000 ALTER TABLE `workflowmasters` DISABLE KEYS */;
/*!40000 ALTER TABLE `workflowmasters` ENABLE KEYS */;

--
-- Table structure for table `workflowstages`
--

DROP TABLE IF EXISTS `workflowstages`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `workflowstages` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `WorkflowId` int NOT NULL,
  `StageOrder` int NOT NULL,
  `Name` varchar(180) NOT NULL,
  `ApproverType` varchar(40) NOT NULL,
  `ApproverUserId` int DEFAULT NULL,
  PRIMARY KEY (`Id`),
  KEY `FK_WorkflowStages_Master` (`WorkflowId`),
  CONSTRAINT `FK_WorkflowStages_Master` FOREIGN KEY (`WorkflowId`) REFERENCES `workflowmasters` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=8 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `workflowstages`
--

/*!40000 ALTER TABLE `workflowstages` DISABLE KEYS */;
/*!40000 ALTER TABLE `workflowstages` ENABLE KEYS */;

--
-- Table structure for table `workflowtasks`
--

DROP TABLE IF EXISTS `workflowtasks`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `workflowtasks` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `InstanceId` bigint NOT NULL,
  `StageId` int NOT NULL,
  `ApproverUserId` int NOT NULL,
  `Status` varchar(30) NOT NULL DEFAULT 'Pending',
  `CreatedAt` datetime DEFAULT CURRENT_TIMESTAMP,
  `ActionedAt` datetime DEFAULT NULL,
  `Comment` varchar(1000) DEFAULT NULL,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `workflowtasks`
--

/*!40000 ALTER TABLE `workflowtasks` DISABLE KEYS */;
/*!40000 ALTER TABLE `workflowtasks` ENABLE KEYS */;

--
-- Table structure for table `worklocations`
--

DROP TABLE IF EXISTS `worklocations`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `worklocations` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ClientId` int NOT NULL DEFAULT '0',
  `ClientName` varchar(250) DEFAULT NULL,
  `Name` varchar(200) NOT NULL,
  `Address` varchar(500) DEFAULT NULL,
  `City` varchar(100) DEFAULT NULL,
  `State` varchar(100) DEFAULT NULL,
  `PostalCode` varchar(30) DEFAULT NULL,
  `IsPrimary` tinyint(1) NOT NULL DEFAULT '0',
  `IsActive` tinyint(1) NOT NULL DEFAULT '1',
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  KEY `IX_WorkLocations_ClientId` (`ClientId`)
) ENGINE=InnoDB AUTO_INCREMENT=67 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `worklocations`
--

/*!40000 ALTER TABLE `worklocations` DISABLE KEYS */;
INSERT INTO `worklocations` VALUES (44,0,NULL,'Head Office','221B Business Park','Bengaluru','Karnataka','560001',1,1,'2026-06-25 22:18:55','2026-06-25 22:18:55'),(45,0,NULL,'Pune Branch','Tower 4, Hinjewadi','Pune','Maharashtra','411057',0,1,'2026-06-25 22:18:55','2026-06-25 22:18:55'),(46,10,'RECL','Bangalore','RECL - Bangalore, Bengaluru Urban, Karnataka','Bengaluru','Karnataka','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(47,10,'RECL','BBN','RECL - BBN, Solan, Himachal Pradesh','Baddi-Barotiwala-Nalagarh','Himachal Pradesh','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(48,10,'RECL','Bhopal','RECL - Bhopal, Bhopal, Madhya Pradesh','Bhopal','Madhya Pradesh','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(49,10,'RECL','Chennai','RECL - Chennai, Chennai, Tamil Nadu','Chennai','Tamil Nadu','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(50,10,'RECL','CO, Gurugram','RECL - CO, Gurugram, Gurugram, Haryana','Gurugram','Haryana','',1,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(51,10,'RECL','GUWAHATI','RECL - GUWAHATI, Kamrup Metropolitan, Assam','Guwahati','Assam','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(52,10,'RECL','JAIPUR','RECL - JAIPUR, Jaipur, Rajasthan','Jaipur','Rajasthan','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(53,10,'RECL','JAMMU','RECL - JAMMU, Jammu, Jammu & Kashmir','Jammu','Jammu & Kashmir','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(54,10,'RECL','KOLKATA','RECL - KOLKATA, Kolkata, West Bengal','Kolkata','West Bengal','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(55,10,'RECL','Lucknow','RECL - Lucknow, Lucknow, Uttar Pradesh','Lucknow','Uttar Pradesh','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(56,10,'RECL','MNRE','RECL - MNRE, New Delhi, Delhi','New Delhi','Delhi','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(57,10,'RECL','Mumbai','RECL - Mumbai, Mumbai, Maharashtra','Mumbai','Maharashtra','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(58,10,'RECL','Panchkula','RECL - Panchkula, Panchkula, Haryana','Panchkula','Haryana','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(59,10,'RECL','Patna','RECL - Patna, Patna, Bihar','Patna','Bihar','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(60,10,'RECL','RAIPUR','RECL - RAIPUR, Raipur, Chhattisgarh','Raipur','Chhattisgarh','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(61,10,'RECL','Ranchi','RECL - Ranchi, Ranchi, Jharkhand','Ranchi','Jharkhand','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(62,10,'RECL','SCOPE Complex, New Delhi','RECL - SCOPE Complex, New Delhi, New Delhi, Delhi','New Delhi','Delhi','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(63,10,'RECL','Shimla','RECL - Shimla, Shimla, Himachal Pradesh','Shimla','Himachal Pradesh','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(64,10,'RECL','Thiruvananthapuram','RECL - Thiruvananthapuram, Thiruvananthapuram, Kerala','Thiruvananthapuram','Kerala','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(65,10,'RECL','Vadodra','RECL - Vadodra, Vadodara, Gujarat','Vadodara','Gujarat','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27'),(66,10,'RECL','Vijayawada','RECL - Vijayawada, NTR, Andhra Pradesh','Vijayawada','Andhra Pradesh','',0,1,'2026-06-28 07:45:27','2026-06-28 07:45:27');
/*!40000 ALTER TABLE `worklocations` ENABLE KEYS */;
/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;

/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;
/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;
/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
/*!40111 SET SQL_NOTES=@OLD_SQL_NOTES */;

-- Dump completed on 2026-06-28  9:47:23

-- Volatile auth/audit tables: schema only, no local session/audit data.

-- MySQL dump 10.13  Distrib 9.6.0, for Win64 (x86_64)
--
-- Host: localhost    Database: payroll
-- ------------------------------------------------------
-- Server version	9.6.0

/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!50503 SET NAMES utf8mb4 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;

--
-- Table structure for table `authsessions`
--

DROP TABLE IF EXISTS `authsessions`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `authsessions` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `UserId` int NOT NULL,
  `TokenHash` char(64) NOT NULL,
  `IpAddress` varchar(80) DEFAULT NULL,
  `UserAgent` varchar(500) DEFAULT NULL,
  `ExpiresAt` datetime NOT NULL,
  `RevokedAt` datetime DEFAULT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_AuthSessions_TokenHash` (`TokenHash`),
  KEY `IX_AuthSessions_User` (`UserId`),
  CONSTRAINT `FK_AuthSessions_User` FOREIGN KEY (`UserId`) REFERENCES `authusers` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=70 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `auditlogs`
--

DROP TABLE IF EXISTS `auditlogs`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `auditlogs` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `UserId` int DEFAULT NULL,
  `UserEmail` varchar(190) DEFAULT NULL,
  `Action` varchar(120) NOT NULL,
  `Resource` varchar(190) DEFAULT NULL,
  `Method` varchar(20) DEFAULT NULL,
  `Path` varchar(500) DEFAULT NULL,
  `StatusCode` int NOT NULL DEFAULT '0',
  `IpAddress` varchar(80) DEFAULT NULL,
  `UserAgent` varchar(500) DEFAULT NULL,
  `DetailsJson` json NOT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  KEY `IX_AuditLogs_CreatedAt` (`CreatedAt`),
  KEY `IX_AuditLogs_UserId` (`UserId`),
  KEY `IX_AuditLogs_Action` (`Action`)
) ENGINE=InnoDB AUTO_INCREMENT=227 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;
/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;

/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;
/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;
/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
/*!40111 SET SQL_NOTES=@OLD_SQL_NOTES */;

-- Dump completed on 2026-06-28  9:47:23

